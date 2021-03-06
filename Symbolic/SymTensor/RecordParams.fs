﻿namespace SymTensor

open System
open System.Reflection
open FSharp.Reflection

open Tensor.Utils
open Tensor
open UExprTypes

type private VarRecordHelpers () =
    static member PublishLocStride<'T when 'T: equality and 'T: comparison> 
            (expr: ExprT) (loc: ITensorDevice) (stride: int64 list option) (mi: ModelInstance<'T>) =
        mi.SetLoc expr loc
        match stride with
        | Some stride -> mi.SetStride expr stride
        | None -> ()
    static member ValueArrayOnDev<'T> (value: 'T) (dev: IDevice) = 
        HostTensor.scalar value |> dev.ToDev :> ITensor
    static member UVarSpecOfExpr<'T> (expr: ExprT) =
        Expr.extractVar expr
    static member WriteArrayToHDF<'T> (hdf: HDF5) (dev: IDevice) (name: string) (value: Tensor<'T>) =
        value |> dev.ToHost |> HostTensor.write hdf name
    static member WriteScalarToHDF<'T> (hdf: HDF5) (dev: IDevice) (name: string) (value: 'T) =
        value |> HostTensor.scalar |> HostTensor.write hdf name
    static member ReadArrayFromHDF<'T> (hdf: HDF5) (dev: IDevice) (name: string) : Tensor<'T> =
        HostTensor.read hdf name |> dev.ToDev
    static member ReadScalarFromHDF<'T> (hdf: HDF5) (dev: IDevice) (name: string) : 'T =
        HostTensor.read hdf name |> Tensor.value

type private ValueType =
    | Scalar of Type
    | Array of Type

type private RFieldInfo = {
    Expr:           obj
    VarSpec:        VarSpecT
    ValueType:      ValueType
}

/// Maps a value record (containing scalars or ArrayNDTs) to a expression record
/// (containing ExprTs).
type VarRecord<'RVal, 'RExpr when 'RVal: equality> (rExpr:      'RExpr,
                                                    dev:        IDevice) =
    do 
        if not (FSharpType.IsRecord typeof<'RVal> && FSharpType.IsRecord typeof<'RExpr>) then
            failwith "'PVal and 'PExpr must both be record types"

    let valFields = FSharpType.GetRecordFields typeof<'RVal>
    let exprFields = FSharpType.GetRecordFields typeof<'RExpr>
    let exprDatas = FSharpValue.GetRecordFields rExpr

    do if Array.length valFields <> Array.length exprFields then
        failwith "'PVal and 'PExpr must both have the same number of fields"

    let fieldInfos = 
        seq {
            for valField, exprField, exprData in Seq.zip3 valFields exprFields exprDatas do
                if valField.Name <> exprField.Name then
                    failwithf "name mismatch for fields %s and %s" valField.Name exprField.Name

                // get value type and corresponding expression type
                let baseType, valueType, exprType =                   
                    if valField.PropertyType.IsGenericType && 
                            valField.PropertyType.GetGenericTypeDefinition() = typedefof<Tensor<_>> then
                        // ArrayNDT<'T> => ExprT
                        let bt = valField.PropertyType.GetGenericArguments().[0]
                        bt, Array bt, typeof<ExprT>
                    else
                        // 'T => ExprT (scalar)
                        let bt = valField.PropertyType
                        bt, Scalar bt, typeof<ExprT>

                if exprField.PropertyType <> exprType then
                    failwithf "type mismatch for field %s: 'PVal type %A requires 'PExpr type %A but got %A"
                        valField.Name valField.PropertyType exprType exprField.PropertyType

                // extract UVarSpecT
                let mi = typeof<VarRecordHelpers>.GetMethod("UVarSpecOfExpr", Util.allBindingFlags) 
                let m = mi.MakeGenericMethod baseType
                let varSpec = m.Invoke(null, [|exprData|]) :?> VarSpecT

                yield {Expr=exprData; VarSpec=varSpec; ValueType=valueType}
        } 

    let mutable varEnvCache = None

    /// the storage device
    member this.Dev  = dev

    /// the expression record
    member this.Expr = rExpr

    /// the VarEnv containing the values in the passed value record
    member this.VarEnv (value: 'RVal) : VarEnvT =        
        match varEnvCache with
        | Some (lastValue, lastVarEnv) when lastValue = value -> lastVarEnv
        | _ ->
            let values = FSharpValue.GetRecordFields value
            let varEnv =
                (VarEnv.empty, Seq.zip fieldInfos values)
                ||> Seq.fold (fun varEnv (fi, value) ->
                    match fi.ValueType with
                    | Scalar baseType ->
                        let mi = typeof<VarRecordHelpers>.GetMethod("ValueArrayOnDev", Util.allBindingFlags) 
                        let m = mi.MakeGenericMethod baseType
                        let valueAry = m.Invoke(null, [|box value; box dev|]) :?> ITensor
                        varEnv |> VarEnv.addVarSpec fi.VarSpec valueAry
                    | Array _ ->
                        varEnv |> VarEnv.addVarSpec fi.VarSpec (value :?> ITensor)
                )
            varEnvCache <- Some (value, varEnv)
            varEnv      
        
    /// extends the given function to accept a value record
    member this.Use (f: VarEnvT -> 'R) =
        fun (ve: VarEnvT) (value: 'RVal) -> f (VarEnv.join ve (this.VarEnv value))

    /// publishes the locations and strides of the used variables to the given ModelInstance
    member this.PublishLocAndStride (model: ModelInstance<'T>) =        
        fieldInfos
        |> Seq.iter (fun fi ->
            let loc = dev.DefaultLoc
            let shp = 
                fi.VarSpec.Shape 
                |> SymSizeEnv.substShape model.CompileEnv.SymSizes
                |> ShapeSpec.tryEval
            let stride = Option.map TensorLayout.cStride shp
            match fi.ValueType with
            | Scalar baseType | Array baseType ->
                let mi = typeof<VarRecordHelpers>.GetMethod("PublishLocStride", Util.allBindingFlags)
                let m = mi.MakeGenericMethod typeof<'T>
                m.Invoke(null, [|fi.Expr; loc; stride; model|]) |> ignore
        )

    /// Saves the record values as a HDF5 file.
    member this.SaveValue hdf prefix (value: 'RVal) =
        let values = FSharpValue.GetRecordFields value
        for fi, value in Seq.zip fieldInfos values do
            match fi.ValueType with
            | Scalar typ ->
                let mi = typeof<VarRecordHelpers>.GetMethod("WriteScalarToHDF", Util.allBindingFlags)
                let m = mi.MakeGenericMethod typ
                m.Invoke(null, [|box hdf; box dev; box (prefix + "/" + fi.VarSpec.Name); value|]) |> ignore
            | Array typ ->
                let mi = typeof<VarRecordHelpers>.GetMethod("WriteArrayToHDF", Util.allBindingFlags)
                let m = mi.MakeGenericMethod typ
                m.Invoke(null, [|box hdf; box dev; box (prefix + "/" + fi.VarSpec.Name); value|]) |> ignore

    /// Load the record value from a HDF5 file using the specifed prefix
    member this.LoadValue hdf prefix : 'RVal =
        let values = seq {
            for fi in fieldInfos do
                match fi.ValueType with
                | Scalar typ ->
                    let mi = typeof<VarRecordHelpers>.GetMethod("ReadScalarFromHDF", Util.allBindingFlags)
                    let m = mi.MakeGenericMethod typ
                    yield m.Invoke(null, [|box hdf; box dev; box (prefix + "/" + fi.VarSpec.Name)|]) 
                | Array typ ->
                    let mi = typeof<VarRecordHelpers>.GetMethod("ReadArrayFromHDF", Util.allBindingFlags)
                    let m = mi.MakeGenericMethod typ
                    yield m.Invoke(null, [|box hdf; box dev; box (prefix + "/" + fi.VarSpec.Name)|])         
        }
        FSharpValue.MakeRecord (typeof<'RVal>, Array.ofSeq values) :?> 'RVal

