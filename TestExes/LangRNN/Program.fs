﻿namespace LangRNN

open System.IO
open Argu

open Basics
open ArrayNDNS
open Models


module Program =

    type CLIArgs = 
        | Generate of int 
        | Train
        | Slack of string
        | TokenLimit of int
        | MaxIters of int
        | BatchSize of int
        | [<Mandatory>] Data of string
        | Steps of int
        | Hiddens of int
        | CheckpointInterval of int
        | DropState of float
        | PrintSamples
        with
        interface IArgParserTemplate with
            member s.Usage =
                match s with
                | Generate _ -> "generates samples from trained model using the specified seed"
                | Train -> "train model"
                | Slack _ -> "connect as a slack bot using the specified key"
                | TokenLimit _ -> "limits the number of training tokens"
                | MaxIters _ -> "limits the number of training epochs"
                | BatchSize _ -> "training batch size"
                | Data _ -> "path to data file"
                | Steps _ -> "number of steps to back-propagate gradient for"
                | Hiddens _ -> "number of hidden units"
                | CheckpointInterval _ -> "number of epochs between writing checkpoint"
                | DropState _ -> "probability of setting latent state to zero at the start of a mini-batch"
                | PrintSamples -> "prints some samples from the training set"

    [<EntryPoint>]
    let main argv = 
        // debug
        Util.disableCrashDialog ()
        //SymTensor.Compiler.Cuda.Debug.ResourceUsage <- true
        //SymTensor.Compiler.Cuda.Debug.SyncAfterEachCudaCall <- true
        SymTensor.Compiler.Cuda.Debug.FastKernelMath <- true
        //SymTensor.Debug.VisualizeUExpr <- true
        //SymTensor.Debug.TraceCompile <- true
        //SymTensor.Debug.Timing <- true
        //SymTensor.Compiler.Cuda.Debug.Timing <- true
        //SymTensor.Compiler.Cuda.Debug.TraceCompile <- true

        // required for SlackBot
        Cuda.CudaSup.setContext ()

        // tests
        //verifyRNNGradientOneHot DevCuda
        //verifyRNNGradientIndexed DevCuda
        //TestUtils.compareTraces verifyRNNGradientIndexed false |> ignore
        //exit 0

        let parser = ArgumentParser.Create<CLIArgs> (helpTextMessage="Language learning RNN",
                                                     errorHandler = ProcessExiter())
        let args = parser.ParseCommandLine argv
        let batchSize = args.GetResult (<@BatchSize@>, 250)
        let stepsPerSmpl = args.GetResult (<@Steps@>, 25)
        let embeddingDim = args.GetResult (<@Hiddens@>, 128)
        let checkpointInterval = args.GetResult (<@CheckpointInterval@>, 10)
        let dropState = args.GetResult (<@DropState@>, 0.0)

        // load data
        let data = WordData (dataPath      = args.GetResult <@Data@>,
                             vocSizeLimit  = None,
                             stepsPerSmpl  = stepsPerSmpl,
                             minSamples    = int (float batchSize / 0.90),
                             tokenLimit    = args.TryGetResult <@TokenLimit@>,
                             useChars      = true)

        // instantiate model
        let model = GRUInst (VocSize      = data.VocSize,
                             EmbeddingDim = embeddingDim)

        // output some training samples
        if args.Contains <@PrintSamples@> then
            for smpl=0 to 3 do
                for i, s in Seq.indexed (data.Dataset.Trn.SlotBatches batchSize stepsPerSmpl) do
                    let words = s.Words.[smpl, *] |> data.ToStr
                    printfn "Batch %d, sample %d:\n%s\n" i smpl words

        // train model or load checkpoint
        printfn "Training with batch size %d and %d steps per slot" batchSize stepsPerSmpl
        let trainCfg = {
            Train.defaultCfg with
                MinIters           = args.TryGetResult <@ MaxIters @>
                //LearningRates      = [1e-3; 1e-4; 1e-5; 1e-6]
                LearningRates      = [1e-4; 1e-5; 1e-6]
                BatchSize          = batchSize
                SlotSize           = Some stepsPerSmpl
                BestOn             = Training
                CheckpointDir      = Some "."
                CheckpointInterval = Some checkpointInterval
                PerformTraining    = args.Contains <@Train@>
        }
        model.Train data.Dataset dropState trainCfg |> ignore

        // generate some word sequences
        match args.TryGetResult <@Generate@> with
        | Some seed ->
            printfn "Generating..."
            let NStart  = 30
            let NPred   = 20

            let rng = System.Random seed
            let allWords = data.Words |> Array.ofList
            let startIdxs = rng.Seq (0, allWords.Length-100) |> Seq.take NPred
        
            let startWords = 
                startIdxs
                |> Seq.map (fun startIdx ->
                    let mutable pos = startIdx
                    if not data.UseChars then
                        while pos+2*NStart >= allWords.Length || 
                              allWords.[pos+NStart-1] <> ">" ||
                              (allWords.[pos .. pos+NStart-1] |> Array.contains "===") do
                            pos <- pos + 1
                            if pos >= allWords.Length then pos <- 0
                    allWords.[pos .. pos+2*NStart-1] |> List.ofArray
                    )
                |> Seq.map data.Tokenize
                |> List.ofSeq
                |> ArrayNDHost.ofList2D

            let genWords = model.Generate 1001 {Words=startWords |> ArrayNDCuda.toDev}
            let genWords = genWords.Words |> ArrayNDHost.fetch
            for s=0 to NPred-1 do
                printfn "======================= Sample %d ====================================" s
                printfn "====> prime:      \n%s" (data.ToStr startWords.[s, 0..NStart-1])
                printfn "\n====> generated:\n> %s" (data.ToStr genWords.[s, *])
                printfn "\n====> original: \n> %s" (data.ToStr startWords.[s, NStart..])
                printfn ""
        | None -> ()

        // slack bot
        match args.TryGetResult <@Slack@> with
        | Some slackKey -> 
            let bot = SlackBot (data, model, slackKey)
            printfn "\nSlackBot is connected. Press Ctrl+C to quit."
            while true do
               Async.Sleep 10000 |> Async.RunSynchronously
        | None -> ()

        // shutdown
        Cuda.CudaSup.shutdown ()
        0 


