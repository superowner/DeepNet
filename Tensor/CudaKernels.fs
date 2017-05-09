﻿namespace Tensor.Cuda.Backend

open System
open System.IO
open System.Threading
open System.Reflection
open System.Reflection.Emit
open System.Runtime.InteropServices
open System.Security.Cryptography
open ManagedCuda
open ManagedCuda.BasicTypes

open Tensor.Utils


/// CUDA backend configuration
module Cfg = 

    let mutable FastKernelMath = false
    let mutable RestrictKernels = false
    let mutable DebugCompile = true
    let mutable DisableKernelCache = false


/// Dynamic type helpers.
module internal DynamicTypes =
    let currentDomain = Thread.GetDomain()
    let dynAsmName = new AssemblyName("TensorDynamicTypes")
    //dynAsmName.Name <- "ArrayByValDynamicTypes"
    let asmBuilder = currentDomain.DefineDynamicAssembly(dynAsmName, AssemblyBuilderAccess.Run)
    let modBuilder = asmBuilder.DefineDynamicModule("Module")


/// C++ tensor marshaling
type internal NativeTensor = {
    DataType:       Type
    BasePtr:        nativeint
    Offset:         int64
    Shape:          int64 list
    Stride:         int64 list
}
   
/// C++ tensor marshaling
type internal NativeTensorInfo = {
    DataType:       Type
    NDims:          int
}

/// C++ tensor marshaling
module internal NativeTensor =
    let private typeCache = Dictionary<string, Type> ()

    /// creates a struct type containing a fixed size array of given type and size
    let private getType (dataType: Type) (nDims: int) =
        let typeName = sprintf "Tensor_%s_%d" dataType.Name nDims
        match typeCache.TryFind typeName with
        | Some typ -> typ
        | None ->
            lock DynamicTypes.modBuilder (fun () ->
                // define new value type with attribute [<Struct; StructLayout(LayoutKind.Sequential)>]
                let mb = DynamicTypes.modBuilder
                let tb = mb.DefineType(typeName, 
                                       TypeAttributes.Public ||| TypeAttributes.SequentialLayout,
                                       typeof<ValueType>)

                // define fields
                tb.DefineField("Base", typeof<nativeint>, FieldAttributes.Public) |> ignore
                tb.DefineField("Offset", typeof<int64>, FieldAttributes.Public) |> ignore
                for d = 0 to nDims - 1 do
                    tb.DefineField(sprintf "Shape%d" d, typeof<int64>, FieldAttributes.Public) |> ignore
                for d = 0 to nDims - 1 do
                    tb.DefineField(sprintf "Stride%d" d, typeof<int64>, FieldAttributes.Public) |> ignore

                // create defined type and cache it
                let typ = tb.CreateType()
                typeCache.[typeName] <- typ
                typ
            )

    /// C++ Tensor<T, nDims> struct ready for marshaling
    let marshal (nt: NativeTensor) =          
        let nDims = nt.Shape.Length
        if nt.Stride.Length <> nDims then
            failwith "shape and stride must have same length"

        // create struct 
        let strctType = getType nt.DataType nDims
        let strct = Activator.CreateInstance(strctType)

        // set data
        strctType.GetField("Base").SetValue(strct, nt.BasePtr)
        strctType.GetField("Offset").SetValue(strct, nt.Offset)
        for d, (size, str) in List.indexed (List.zip nt.Shape nt.Stride) do
            strctType.GetField(sprintf "Shape%d" d).SetValue(strct, size)
            strctType.GetField(sprintf "Stride%d" d).SetValue(strct, str)
        strct

    /// C++ native tensor type string
    let cppName (nti: NativeTensorInfo) =
        sprintf "Tensor<%s, %d>" (Util.cppTypeInst nti.DataType) nti.NDims

    let mangledName (nti: NativeTensorInfo) =
        sprintf "Tensor_%s_%d" (Util.cppTypeInst nti.DataType) nti.NDims

    let validInstance (nti: NativeTensorInfo) (nt: NativeTensor) =
        nt.DataType = nti.DataType && 
        nt.Shape.Length = nti.NDims && 
        nt.Stride.Length = nti.NDims


/// CUDA module caching key.
type internal ModCacheKey = {
    Code:           string
    HeaderHashes:   Map<string, byte list>
    CompilerArgs:   string list
}

/// compiles CUDA C++ code to CUDA kernels.
module internal KernelCompiler =
    let krnlPtxCacheDir = Path.Combine(Util.localAppData "Tensor", "PTXCache")
    let krnlPtxCache = DiskMap<ModCacheKey, byte[]> (krnlPtxCacheDir, "code.dat", "mod.ptx")
    let compileDirRoot = Path.Combine(Util.localAppData "Tensor", "Compile")

    /// prepares a compile directory
    let private prepareCompileDir code =        
        // create temp directory
        let rec getTempDir () =  
            let dirname = Path.Combine(compileDirRoot, Path.GetRandomFileName())
            if not (Directory.Exists dirname) then dirname
            else getTempDir()
        let compileDir = getTempDir ()
        Directory.CreateDirectory compileDir |> ignore

        // get embedded header files from out assembly
        let asmbly = Assembly.GetExecutingAssembly()
        let headers =
            asmbly.GetManifestResourceNames ()
            |> Seq.filter (fun s -> s.EndsWith(".cuh") || s.EndsWith(".h"))

        // calculate MD5 sum of headers
        let headerHashes =
            headers
            |> Seq.map (fun header -> 
                use strm = asmbly.GetManifestResourceStream(header)
                use md5 = MD5.Create()
                (header, md5.ComputeHash strm |> Array.toList))
            |> Map.ofSeq

        // write headers to compile directory
        headers
        |> Seq.iter (fun header -> 
            use strm = asmbly.GetManifestResourceStream(header)
            let filename = Path.Combine (compileDir, header)
            use fileStrm = File.OpenWrite filename
            strm.CopyTo fileStrm)

        // write module code
        let modPath = Path.Combine (compileDir, "mod.cu")
        File.WriteAllText(modPath, code)

        compileDir, modPath, headerHashes

    /// removes a compile directory
    let private removeCompileDir compileDir =
        Directory.Delete(compileDir, true)     

    /// Compiles the given CUDA device code into a CUDA module, loads and jits it and returns
    /// ManagedCuda.CudaKernel objects for the specified kernel names.
    let load modCode krnlNames =
        let compileDir, modPath, headerHashes = prepareCompileDir modCode

        use cmplr = new NVRTC.CudaRuntimeCompiler(modCode, modPath) 
        let baseCmplrArgs = [
            yield "--std=c++11"
            yield "-DWIN32_LEAN_AND_MEAN"
            yield "-Xcudafe"; yield "--diag_suppress=declared_but_not_referenced"
            yield sprintf "--gpu-architecture=%s" Cuda.nvccArch
            if Cfg.FastKernelMath then yield "--use_fast_math"
            if Cfg.RestrictKernels then yield "--restrict"
            if Cfg.DebugCompile then yield "--device-debug"
            if Cfg.DebugCompile then yield "--generate-line-info"
        ] 
        let cmplrArgs =
            baseCmplrArgs @ [sprintf "--include-path=\"%s\"" compileDir]

        let cacheKey = {Code=modCode; HeaderHashes=headerHashes; CompilerArgs=baseCmplrArgs}
        let ptx =
            match krnlPtxCache.TryGet cacheKey with
            | Some ptx when not Cfg.DisableKernelCache -> ptx
            | _ ->
                if Cfg.DebugCompile then
                   printfn "nvrtc %s %s" (cmplrArgs |> String.concat " ") modPath 
                try cmplr.Compile (Array.ofList cmplrArgs)
                with :? NVRTC.NVRTCException as cmplrError ->
                    let log = cmplr.GetLogAsString()
                    let log = log.Replace ("\n\n", "\n")
                    failwithf "nvrtc compile error:\n%s" log
                if Cfg.DebugCompile then
                    let log = cmplr.GetLogAsString()
                    printf "%s" log
                let ptx = cmplr.GetPTX()
                krnlPtxCache.Set cacheKey ptx                
                ptx    

        if not Cfg.DebugCompile then 
            removeCompileDir compileDir
      
        use jitOpts = new CudaJitOptionCollection()
        use jitInfoBuffer = new CudaJOInfoLogBuffer(10000)
        jitOpts.Add(jitInfoBuffer)
        use jitErrorBuffer = new CudaJOErrorLogBuffer(10000)   
        jitOpts.Add(jitErrorBuffer)
        use jitLogVerbose = new CudaJOLogVerbose(true)
        jitOpts.Add(jitLogVerbose)

        let cuMod = Cuda.context.LoadModulePTX(ptx, jitOpts)

        jitOpts.UpdateValues()
        if Cfg.DebugCompile then
            printfn "%s" jitErrorBuffer.Value
            printfn "%s" jitInfoBuffer.Value   
        jitErrorBuffer.FreeHandle()
        jitInfoBuffer.FreeHandle()

        let krnls =
            (Map.empty, krnlNames)
            ||> Seq.fold (fun krnls name -> 
                krnls |> Map.add name (CudaKernel(name, cuMod, Cuda.context))) 

        krnls, cuMod

    /// unloads previously loaded CUDA kernel code
    let unload cuMod =
        Cuda.context.UnloadModule(cuMod)

    
/// Argument type of a CUDA kernel
type internal KernelArgType = 
    | ArgTypeTensor of NativeTensorInfo
    | ArgTypeInt64

/// Argument type of a CUDA kernel
module internal KernelArgType =
    let cppType at =
        match at with
        | ArgTypeTensor nti -> NativeTensor.cppName nti
        | ArgTypeInt64 -> "int64_t"

    let mangleName at =
        match at with
        | ArgTypeTensor nti -> NativeTensor.mangledName nti
        | ArgTypeInt64 -> "int64_t"

    let marshal at (av: obj) =
        match at, av with
        | ArgTypeTensor nti, (:? NativeTensor as nt) when NativeTensor.validInstance nti nt ->
            NativeTensor.marshal nt
        | ArgTypeInt64, (:? int64 as v) -> box v
        | _ -> failwithf "cannot marshal %A as %A" av at

/// A CUDA module built from source containing kernel functions.
type internal CudaModule () =
    let wrapperCodes = Dictionary<string, string> ()
    let mutable kernels : Map<string, CudaKernel> option = None
    let mutable cuMod = None

    member this.GetKernel (funcName: string) (argTypes: KernelArgType list) =
        let mangledArgTypes =
            argTypes
            |> List.map KernelArgType.mangleName
            |> String.concat "__"
        let mangledName = funcName + "__" + mangledArgTypes
        let argDeclStr =
            argTypes
            |> List.mapi (fun i typ -> sprintf "%s arg%d" (KernelArgType.cppType typ) i)
            |> String.concat ", "
        let argCallStr = 
            argTypes
            |> List.mapi (fun i _ -> sprintf "arg%d" i)
            |> String.concat ", "
        let declCode =
            sprintf "extern \"C\" __global__ void %s (%s)" mangledName argDeclStr
        let callCode = 
            sprintf "%s (%s);" funcName argCallStr
        let wrapperCode =
            sprintf "%s { %s }\n" declCode callCode
        wrapperCodes.[mangledName] <- wrapperCode

        let argTypes = List.toArray argTypes
        (fun (stream: CUstream, workDim: Cuda.WorkDim, [<ParamArray>] argVals: obj[]) ->
            match kernels with
            | Some kernels ->
                let kernel = kernels.[mangledName]
                let maxBlockSize = kernel.GetOccupancyMaxPotentialBlockSize().blockSize
                let launchDim = Cuda.computeLaunchDim workDim maxBlockSize
                kernel.BlockDimensions <- Cuda.toDim3 launchDim.Block
                kernel.GridDimensions <- Cuda.toDim3 launchDim.Grid
                kernel.DynamicSharedMemory <- 0u

                if argVals.Length <> argTypes.Length then
                    failwith "incorrect number of arguments"
                let kernelArgs =
                    (argTypes, argVals) ||> Array.map2 KernelArgType.marshal
                kernel.RunAsync (stream, kernelArgs)
            | None -> failwith "KernelFunc was not built"
        )

    member this.Build (headers: string list) =
        match kernels with
        | Some _ -> failwith "KernelFuncs already built"
        | None -> 
            let headerCode = 
                headers 
                |> List.map (fun h -> sprintf "#include \"%s\"" h)
                |> String.concat "\n"
            let wrapperCode =
                wrapperCodes
                |> Seq.map (fun (KeyValue(_, code)) -> code)
                |> String.concat "\n"
            let code = headerCode + "\n" + wrapperCode
            let wrapperNames = 
                wrapperCodes
                |> Seq.map (fun (KeyValue(name, _)) -> name)
            let krnls, m = KernelCompiler.load code wrapperNames
            kernels <- Some krnls
            cuMod <- Some m
            
    override this.Finalize() =
        match cuMod with
        | Some cm ->
            try KernelCompiler.unload cm
            with _ -> ()
            cuMod <- None
        | None -> ()



type internal TensorKernels (dataType: Type, nDims: int) as this =
    inherit CudaModule()
    static let headers = ["CudaTensor.cuh"]

    let fullTensor = ArgTypeTensor {DataType=dataType; NDims=nDims}

    let copyFunc =
        this.GetKernel "Copy" [fullTensor; fullTensor]

    do this.Build (headers)

    member this.Copy (stream, workDim, trgt: NativeTensor, src: NativeTensor) = 
        copyFunc (stream, workDim, [|box trgt; box src|])




        

        