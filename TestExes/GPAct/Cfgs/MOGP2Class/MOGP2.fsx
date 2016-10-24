﻿#load "../../../../DeepNet.fsx"
#I "../../bin/Debug"
#r "GPAct.exe"

open Basics
open Models
open SymTensor
open Datasets
open Optimizers
open GPAct


let nHidden1 = SizeSpec.fix 30
let nHidden2 = SizeSpec.fix 30


let cfg = {

    Model = {Layers = [MeanOnlyGPLayer 
                        {MeanOnlyGPLayer.defaultHyperPars with
                            NInput                = ConfigLoader.NInput()
                            NGPs                  = nHidden1
                            NTrnSmpls             = SizeSpec.fix 10
                            MeanFunction          = (fun x -> tanh x)
                            LengthscalesTrainable = true
                            TrnXTrainable         = true
                            TrnTTrainable         = true
                            TrnSigmaTrainable     = false
                            WeightsTrainable      = true
                            LengthscalesInit      = Const 0.4f
                            TrnXInit              = Linspaced (-2.0f, 2.0f)
                            TrnTInit              = Linspaced (-1.0f, 1.0f)
                            TrnSigmaInit          = Const (sqrt 0.1f)
                            WeightsInit           = FanOptimal
                            BiasInit              = Const 0.0f}
                       MeanOnlyGPLayer 
                        {MeanOnlyGPLayer.defaultHyperPars with
                            NInput                = nHidden1
                            NGPs                  = nHidden2
                            NTrnSmpls             = SizeSpec.fix 10
                            MeanFunction          = (fun x -> tanh x)
                            LengthscalesTrainable = true
                            TrnXTrainable         = true
                            TrnTTrainable         = true
                            TrnSigmaTrainable     = false
                            WeightsTrainable      = true
                            LengthscalesInit      = Const 0.4f
                            TrnXInit              = Linspaced (-2.0f, 2.0f)
                            TrnTInit              = Linspaced (-1.0f, 1.0f)
                            TrnSigmaInit          = Const (sqrt 0.1f)
                            WeightsInit           = FanOptimal
                            BiasInit              = Const 0.0f}
                       
                       NeuralLayer
                         {NInput        = nHidden2
                          NOutput       = ConfigLoader.NOutput()
                          TransferFunc  = NeuralLayer.SoftMax
                          WeightsTrainable = true
                          BiasTrainable = true}
                      ]
             Loss   = LossLayer.CrossEntropy
             L1Weight = 0.0f
             L2Weight = 1e-4f}

    //dataset from https://archive.ics.uci.edu/ml/machine-learning-databases/letter-recognition/letter-recognition.data
    Data = {Path       = "../../../../Data/UCI/abalone.txt"
            Parameters = {CsvLoader.DefaultParameters with
                           TargetCols       = [0]
                           IntTreatment     = CsvLoader.IntAsNumerical
                           CategoryEncoding = CsvLoader.OneHot
                           Missing          = CsvLoader.SkipRow}}        
                                            
    Optimizer = Adam Adam.DefaultCfg

    Training = {Train.defaultCfg with 
                 MinIters  = Some 10000
                 BatchSize = System.Int32.MaxValue
                 MaxIters  = None}

    SaveParsDuringTraining = false
    PlotGPsDuringTraining  = true
}

