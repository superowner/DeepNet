﻿open Xunit

open Shape
open Op
//open ExprForwardDiff
open ExprReverseDiff
open OpEval
open ExecUnitsGen
open CudaExecUnits
open StreamGen
open CudaRecipe
open CudaExec
open CudaRegMem


let printExpr label expr =
    printfn "%s :=\n%A\nshape of %s: %A\n" label expr label (shapeOf expr)

let printVal label value =
    printfn "%s =\n%A\nshape of %s: %A\n" label value label (ArrayND.shape value)

type LinearRegression = {a: ExprT; b: ExprT; x: ExprT; t: ExprT;
                         predOut: ExprT; lossOut: ExprT;
                         lossWrtAOut: ExprT; lossWrtBOut: ExprT; lossWrtXOut: ExprT; lossWrtTOut: ExprT;
                         Pred: ExprT; Loss: ExprT}  
let linearRegression () =
    let a = var "a" [symbol "M"; symbol "N"]
    let b = var "b" [symbol "M"]
    let x = var "x" [symbol "N"]
    let t = var "t" [symbol "M"]

    let predOut = var "predOut" [symbol "M"]
    let lossOut = var "lossOut" []
    let lossWrtAOut = var "lossWrtAOut" [fix 1; (symbol "M") * (symbol "N")]
    let lossWrtBOut = var "lossWrtBOut" [fix 1; symbol "M"]
    let lossWrtXOut = var "lossWrtXOut" [fix 1; symbol "N"]
    let lossWrtTOut = var "lossWrtTOut" [fix 1; symbol "M"]

    let pred = a.*x + b
    let smplLoss = (pred - t)**2.0f
    let loss = sum smplLoss

    {a=a; b=b; x=x; t=t; Pred=pred; Loss=loss;
     predOut=predOut; lossOut=lossOut;
     lossWrtAOut=lossWrtAOut; lossWrtBOut=lossWrtBOut; lossWrtXOut=lossWrtXOut; lossWrtTOut=lossWrtTOut}

type LinearRegressionGradient = {LossWrtA: ExprT; LossWrtB: ExprT; LossWrtX: ExprT; LossWrtT: ExprT}
//let linearRegressionForwardGradient (lr: LinearRegression) =
//    {LossWrtA = grad lr.a lr.Loss;
//     LossWrtB = grad lr.b lr.Loss;
//     LossWrtX = grad lr.x lr.Loss;
//     LossWrtT = grad lr.t lr.Loss;}

let linearRegressionReverseGradient (lr: LinearRegression) =
    let d = reverseDiff lr.Loss
    {LossWrtA = diffOf lr.a d;
     LossWrtB = diffOf lr.b d;
     LossWrtX = diffOf lr.x d;
     LossWrtT = diffOf lr.t d;}

let linearRegressionEvalEnv (lr: LinearRegression) =
    let m, n = 3, 2
    let aVal = ArrayND.identity [m; n]
    let bVal = ArrayND.zeros [m]
    let xVal = ArrayND.ones [n]
    let tVal = ArrayND.ones [m]
    let predOutVal = ArrayND.zeros [m]
    let lossOutVal = ArrayND.zeros []
    let lossWrtAVal = ArrayND.zeros [1; m*n]
    let lossWrtBVal = ArrayND.zeros [1; m]
    let lossWrtXVal = ArrayND.zeros [1; n]
    let lossWrtTVal = ArrayND.zeros [1; m]
    let varEnv = 
        VarEnv.empty
        |> VarEnv.add lr.a aVal
        |> VarEnv.add lr.b bVal
        |> VarEnv.add lr.x xVal
        |> VarEnv.add lr.t tVal
        |> VarEnv.add lr.predOut predOutVal
        |> VarEnv.add lr.lossOut lossOutVal
        |> VarEnv.add lr.lossWrtAOut lossWrtAVal
        |> VarEnv.add lr.lossWrtBOut lossWrtBVal
        |> VarEnv.add lr.lossWrtXOut lossWrtXVal
        |> VarEnv.add lr.lossWrtTOut lossWrtTVal
    EvalEnv.fromVarEnvAndExpr varEnv lr.Loss

let linearRegressionCudaEnv (lr: LinearRegression) =
    let varLocs =
        [lr.a |> extractVar, HostVar;
         lr.b |> extractVar, HostVar;
         lr.x |> extractVar, HostVar;
         lr.t |> extractVar, HostVar;
         lr.predOut |> extractVar, HostVar;
         lr.lossOut |> extractVar, HostVar;
         lr.lossWrtAOut |> extractVar, HostVar;
         lr.lossWrtBOut |> extractVar, HostVar;
         lr.lossWrtXOut |> extractVar, HostVar;
         lr.lossWrtTOut |> extractVar, HostVar;]
        |> Map.ofList
    {CudaEnvT.VarStorLoc = varLocs}


[<Fact>]
let ``Build linear regression`` () =
    let lr = linearRegression ()
    printExpr "pred" lr.Pred
    printExpr "loss" lr.Loss

[<Fact>]
let ``Eval linear regression`` () =
    let lr = linearRegression ()
    let env = linearRegressionEvalEnv lr
    printVal "pred" (eval env lr.Pred)
    printVal "loss" (eval env lr.Loss)

//[<Fact>]
//let ``Forward gradient of linear regression`` () =
//    let lr = linearRegression ()   
//    printfn "Forward:"
//    let fg = linearRegressionForwardGradient lr
//    printExpr "lossWrtA" fg.LossWrtA
//    printExpr "lossWrtB" fg.LossWrtB
//    printExpr "lossWrtX" fg.LossWrtX  
//    printExpr "lossWrtT" fg.LossWrtT

[<Fact>]
let ``Reverse gradient of linear regression`` () =
    let lr = linearRegression ()  
    printfn "Reverse:"
    let rg = linearRegressionReverseGradient lr
    printExpr "lossWrtA" rg.LossWrtA
    printExpr "lossWrtB" rg.LossWrtB
    printExpr "lossWrtX" rg.LossWrtX  
    printExpr "lossWrtT" rg.LossWrtT

//[<Fact>]
//let ``Eval forward gradient of linear regression`` () =
//    let lr = linearRegression ()
//    let lrg = linearRegressionForwardGradient lr
//    let env = linearRegressionEvalEnv lr
//    printfn "Forward gradient:"
//    printVal "lossWrtA" (eval env lrg.LossWrtA)
//    printVal "lossWrtB" (eval env lrg.LossWrtB)
//    printVal "lossWrtX" (eval env lrg.LossWrtX) 
//    printVal "lossWrtT" (eval env lrg.LossWrtT)

[<Fact>]
let ``Eval reverse gradient of linear regression`` () =
    let lr = linearRegression ()
    let lrg = linearRegressionReverseGradient lr
    let env = linearRegressionEvalEnv lr
    printfn "Reverse gradient:"
    printVal "lossWrtA" (eval env lrg.LossWrtA)
    printVal "lossWrtB" (eval env lrg.LossWrtB)
    printVal "lossWrtX" (eval env lrg.LossWrtX) 
    printVal "lossWrtT" (eval env lrg.LossWrtT)


//[<Fact>]
//let ``Check forward gradient of linear regression`` () =
//    let lr = linearRegression ()
//    let env = linearRegressionEvalEnv lr
//    printfn "delta lossWrtA = %f" (NumGrad.exprGradDiff env lr.a lr.Loss)
//    printfn "delta lossWrtB = %f" (NumGrad.exprGradDiff env lr.b lr.Loss)
//    printfn "delta lossWrtX = %f" (NumGrad.exprGradDiff env lr.x lr.Loss)
//    printfn "delta lossWrtT = %f" (NumGrad.exprGradDiff env lr.t lr.Loss)

[<Fact>]
let ``Check reverse gradient of linear regression`` () =
    let lr = linearRegression ()
    let env = linearRegressionEvalEnv lr
    DiffCheck.checkReverseDiff env lr.Loss
    printfn "linear regression gradient checked"

let printList execSeq =
    for i, item in List.indexed execSeq do
        printfn "%d. %A" (i+1) item

let printStreams streams =
    for i, stream in List.indexed streams do
        printfn "==============================================="
        printfn "stream %d:" i
        printList stream

[<Fact>]
let ``Build execution sequence of linear regression`` () =
    let lr = linearRegression ()
    let env = linearRegressionEvalEnv lr
    let cenv = linearRegressionCudaEnv lr
    
    let exeSeq, eRes, memAllocs = exprToCudaExecUnits cenv env.SizeSymbolEnv (toUExpr lr.Loss)
    printfn "linear regression exec sequence:\n%A" exeSeq

    let exeStreams, strmCnt = execUnitsToStreamCommands exeSeq
    printfn "linear regression exec streams:"
    printStreams exeStreams

    let cudaCalls, krnlCache = generateCalls exeStreams
    printfn "linear regression CUDA calls:"
    printList cudaCalls


[<Fact>]
let ``Build execution sequence of linear regression gradient`` () =
    let lr = linearRegression ()
    let lrg = linearRegressionReverseGradient lr
    let env = linearRegressionEvalEnv lr
    let cenv = linearRegressionCudaEnv lr

    let exeSeq, eRes, memAllocs = exprToCudaExecUnits cenv env.SizeSymbolEnv (toUExpr lrg.LossWrtA)
    //printfn "linear regression wrt A exec sequence:\n%A" exeSeq

    let exeStreams, strmCnt = execUnitsToStreamCommands exeSeq
    printfn "linear regression wrt A exec streams:"
    printStreams exeStreams

    let cudaCalls, krnlCache = generateCalls exeStreams
    printfn "linear regression wrt A CUDA calls:"
    printList cudaCalls


[<Fact>]
let ``Build CUDA recipe for linear regression gradient`` () =
    let lr = linearRegression ()
    let lrg = linearRegressionReverseGradient lr
    let env = linearRegressionEvalEnv lr
    let cenv = linearRegressionCudaEnv lr

    let recipe = buildCudaRecipe cenv env.SizeSymbolEnv (toUExpr lrg.LossWrtA)
    printfn "%A" recipe

    ()


let ``Evaluate linear regression using CUDA`` () =
    let lr = linearRegression ()
    let env = linearRegressionEvalEnv lr
    let cenv = linearRegressionCudaEnv lr

    let allWrtsSaved = 
        discard [lr.Pred |> storeToVar lr.predOut;
                 lr.Loss |> storeToVar lr.lossOut;]

    //printfn "%A" allWrtsSaved

    let recipe = buildCudaRecipe cenv env.SizeSymbolEnv (toUExpr allWrtsSaved)
    use cudaExpr = new CudaExprWorkspace(recipe)
    use lockedVarEnv = new VarEnvLock(env.VarEnv)

    cudaExpr.Eval(Map.empty, env.VarEnv)

    printVal "pred" (VarEnv.get lr.predOut env.VarEnv)
    printVal "loss" (VarEnv.get lr.lossOut env.VarEnv)


[<Fact>]
let ``Evaluate linear regression gradient using CUDA`` () =
    let lr = linearRegression ()
    let lrg = linearRegressionReverseGradient lr
    let env = linearRegressionEvalEnv lr
    let cenv = linearRegressionCudaEnv lr

    let allWrtsSaved = 
        discard [lrg.LossWrtA |> storeToVar lr.lossWrtAOut;
                 lrg.LossWrtB |> storeToVar lr.lossWrtBOut;
                 lrg.LossWrtX |> storeToVar lr.lossWrtXOut;
                 lrg.LossWrtT |> storeToVar lr.lossWrtTOut]

    //printfn "%A" allWrtsSaved

    let recipe = buildCudaRecipe cenv env.SizeSymbolEnv (toUExpr allWrtsSaved)
    use cudaExpr = new CudaExprWorkspace(recipe)
    use lockedVarEnv = new VarEnvLock(env.VarEnv)

    cudaExpr.Eval(Map.empty, env.VarEnv)

    printVal "lossWrtA" (VarEnv.get lr.lossWrtAOut env.VarEnv)
    printVal "lossWrtB" (VarEnv.get lr.lossWrtBOut env.VarEnv)
    printVal "lossWrtX" (VarEnv.get lr.lossWrtXOut env.VarEnv)
    printVal "lossWrtT" (VarEnv.get lr.lossWrtTOut env.VarEnv)




[<EntryPoint>]
let main argv = 
    CudaBasics.printCudaInfo ()

    //``Build linear regression`` ()
    //``Reverse gradient of linear regression`` ()
    //``Eval forward gradient of linear regression`` ()
    //``Check gradient of linear regression`` ()
    //``Check reverse gradient of linear regression`` ()
    //``Build execution sequence of linear regression`` ()
    //``Build execution sequence of linear regression gradient`` ()
    //``Build CUDA recipe for linear regression gradient`` ()
    
    ``Eval linear regression`` ()
    ``Evaluate linear regression using CUDA`` ()

    ``Eval reverse gradient of linear regression`` ()
    ``Evaluate linear regression gradient using CUDA`` ()
    
    
    CudaBasics.shutdown ()
    0