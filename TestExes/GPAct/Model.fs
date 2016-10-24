﻿namespace GPAct

open ArrayNDNS
open SymTensor
open Basics
open Models


[<AutoOpen>]
module GPUtilsTypes =

    /// initialization types
    type InitMethod =
        /// constant value
        | Const of value:single
        /// linear spaced
        | Linspaced of first:single * last:single
        /// random
        | Random of lower:single * upper:single
        /// identity matrix
        | IdentityMatrix
        /// fan-in/out optimal random weight matrix for neurons
        | FanOptimal


module GPUtils =

    /// calculates initialization values
    let initVals initType seed shp =
        let rng = System.Random seed            
        match initType with
        | Const value -> ArrayNDHost.filled shp value
        | Linspaced (first, last) -> 
            ArrayNDHost.linSpaced first last shp.[1]
            |> ArrayND.padLeft
            |> ArrayND.replicate 0 shp.[0]
        | Random (lower, upper) ->
            rng.UniformArrayND (lower, upper) shp
        | IdentityMatrix ->
            match shp with
            | [n; m] when n = m -> ArrayNDHost.identity n
            | _ -> failwith "need square matrix shape for identity matrix initialization"
        | FanOptimal ->
            let fanOut = shp.[0] |> single
            let fanIn = shp.[1] |> single
            let r = 4.0f * sqrt (6.0f / (fanIn + fanOut))
            rng.UniformArrayND (-r, r) shp

    /// Allows the gradient to pass if trainable is true.
    let gate trainable expr =
        if trainable then expr else Expr.assumeZeroDerivative expr

    /// creates a zero covariance matrix for the given input.
    let covZero input =
        // input [smpl, unit]
        let nSmpls = (Expr.shapeOf input).[0]
        let nInput = (Expr.shapeOf input).[1]
        // [smpl,inp1,1] .* [smpl,1,in2] => [smpl,in1,in2]
        // is equivalent to [smpl,inp1,1*] * [smpl,1*,in2] => [smpl,in1,in2]
        Expr.zeros<single> [nSmpls; nInput; nInput]


open GPUtils

/// propagates normal distributions through non-linearities described by GPs
module GPActivation =

    /// Hyper-parameters
    type HyperPars = {
        /// number of GPs <= number of outputs and inputs
        NGPs:                   SizeSpecT

        ///numberOfOutputs
        NOutput:                SizeSpecT

        /// number of training points for each GP
        NTrnSmpls:              SizeSpecT

        CutOutsideRange:        bool

        LengthscalesTrainable:  bool
        TrnXTrainable:          bool
        TrnTTrainable:          bool
        TrnSigmaTrainable:      bool

        LengthscalesInit:       InitMethod
        TrnXInit:               InitMethod
        TrnTInit:               InitMethod
        TrnSigmaInit:           InitMethod


    }

    /// default hyper-parameters
    let defaultHyperPars = {
        NGPs                  = SizeSpec.fix 0
        NOutput               = SizeSpec.fix 0
        NTrnSmpls             = SizeSpec.fix 10
        CutOutsideRange       = false
        LengthscalesTrainable = true
        TrnXTrainable         = true
        TrnTTrainable         = true
        TrnSigmaTrainable     = true
        LengthscalesInit      = Const 0.4f
        TrnXInit              = Linspaced (-2.0f, 2.0f)
        TrnTInit              = Linspaced (-2.0f, 2.0f)
        TrnSigmaInit          = Const (sqrt 0.1f)
    }

    /// Parameter expressions.
    type Pars = {
        /// GP lengthscales: [gp]
        Lengthscales:       ExprT 
        /// x values of GP training samples:         [gp, trn_smpl]
        TrnX:               ExprT 
        /// target values of GP training samples:    [gp, trn_smpl]
        TrnT:               ExprT 
        /// standard deviation of GP target values:  [gp, trn_smpl]
        TrnSigma:           ExprT 
        /// hyper-parameters
        HyperPars:          HyperPars
    }
    
    /// creates parameters
    let pars (mb: ModelBuilder<_>) hp = {
        Lengthscales   = mb.Param ("Lengthscales", [hp.NGPs],               GPUtils.initVals hp.LengthscalesInit) 
        TrnX           = mb.Param ("TrnX",         [hp.NOutput; hp.NTrnSmpls], GPUtils.initVals hp.TrnXInit)
        TrnT           = mb.Param ("TrnT",         [hp.NOutput; hp.NTrnSmpls], GPUtils.initVals hp.TrnTInit)
        TrnSigma       = mb.Param ("TrnSigma",     [hp.NOutput; hp.NTrnSmpls], GPUtils.initVals hp.TrnSigmaInit)
        HyperPars      = hp
    }

        ///The covariance Matrices of the training vectors with themselves 
    ///by GP instances with squared exponential covariance.
    let Kk nGps nTrnSmpls lengthscales trnX trnSigma = 
        // Kse element expression
        // input  x[gp, trn_smpl]
        //        l[gp]
        //        s[gp, trn_smpl]
        // output cov[gp, trn_smpl1, trn_smpl2]
        let gp, trn_smpl1, trn_smpl2 = ElemExpr.idx3   
        let l, x, s = ElemExpr.arg3<single>
        let kse =
            exp (- ((x [gp; trn_smpl1] - x [gp; trn_smpl2])***2.0f) / (2.0f * (l [gp])***2.0f) ) +
            ElemExpr.ifThenElse trn_smpl1 trn_smpl2 (s [gp; trn_smpl1] *** 2.0f) (ElemExpr.scalar 0.0f)
        
        Expr.elements [nGps; nTrnSmpls; nTrnSmpls] kse [lengthscales; trnX; trnSigma]

    ///The covariance of training vectors and input vector 
    ///by GP instances with squared exponential covariance.
    let lk nSmpls nGps nTrnSmpls mu sigma lengthscales trnX =
        // lk element expression
        // inputs  l[gp]
        //         x[gp, trn_smpl]
        //         m[smpl, gp]        -- mu
        //         s[smpl, gp1, gp2]  -- Sigma
        // output lk[smpl, gp, trn_smpl]
        let smpl = ElemExpr.idx 0
        let gp = ElemExpr.idx 1
        let trn_smpl = ElemExpr.idx 2
        let m = ElemExpr.argElem<single> 0
        let s = ElemExpr.argElem<single> 1
        let l = ElemExpr.argElem<single> 2
        let x = ElemExpr.argElem<single> 3

        let lk1 = sqrt ( (l [gp])***2.0f / ((l [gp])***2.0f + s [smpl; gp; gp]) )
        let lk2 = exp ( -( (m [smpl; gp] - x [gp; trn_smpl])***2.0f / (2.0f * ((l [gp])***2.0f + s [smpl; gp; gp])) ) )
        let lk = lk1 * lk2

        Expr.elements [nSmpls; nGps; nTrnSmpls] lk [mu; sigma; lengthscales; trnX]


    ///Elementwise Matrix needed for calculation of the varance prediction.
    let L nSmpls nGps nTrnSmpls mu sigma lengthscales trnX =
        // L element expression
        // inputs  l[gp]
        //         x[gp, trn_smpl]
        //         m[smpl, gp]        -- mu
        //         s[smpl, gp1, gp2]  -- Sigma
        // output  L[smpl, gp, trn_smpl1, trn_smpl2]
        let smpl = ElemExpr.idx 0
        let gp = ElemExpr.idx 1
        let trn_smpl1 = ElemExpr.idx 2
        let trn_smpl2 = ElemExpr.idx 3
        let m = ElemExpr.argElem<single> 0
        let s = ElemExpr.argElem<single> 1
        let l = ElemExpr.argElem<single> 2
        let x = ElemExpr.argElem<single> 3

        let L1 = sqrt ( (l [gp])***2.0f / ((l [gp])***2.0f + 2.0f * s [smpl; gp; gp]) )
        let L2a = ( m [smpl; gp] - (x [gp; trn_smpl1] + x [gp; trn_smpl2])/2.0f )***2.0f / ((l [gp])***2.0f + 2.0f * s [smpl; gp; gp])
        let L2b = (x [gp; trn_smpl1] - x [gp; trn_smpl2])***2.0f / (4.0f * (l [gp])***2.0f)
        let L2 = exp (-L2a - L2b)
        let L = L1 * L2

        Expr.elements [nSmpls; nGps; nTrnSmpls; nTrnSmpls] L [mu; sigma; lengthscales; trnX]


    ///Elementwise Matrix needed for calculation of the covarance prediction.
    let Tnew nSmpls nGps nTrnSmpls mu sigma lengthscales trnX =
        // T element expression
        // inputs  l[gp]
        //         x[gp, trn_smpl]
        //         m[smpl, gp]        -- mu
        //         s[smpl, gp1, gp2]  -- Sigma
        // output  T[smpl, gp1, gp2, trn_smpl1, trn_smpl2]

        let smpl = ElemExpr.idx 0
        let gp1 = ElemExpr.idx 1
        let gp2 = ElemExpr.idx 2
        let t1 = ElemExpr.idx 3
        let t2 = ElemExpr.idx 4
        let m = ElemExpr.argElem<single> 0
        let s = ElemExpr.argElem<single> 1
        let l = ElemExpr.argElem<single> 2
        let x = ElemExpr.argElem<single> 3

        // Mathematica: k = gp1  l = gp2   i=t1   j=t2

        let eNom = (x[gp2;t2]-m[smpl;gp2])***2.f * (l[gp1]***2.f+s[smpl;gp1;gp1]) + (x[gp1;t1]-m[smpl;gp1]) * 
                   ( 2.f * (m[smpl;gp2]-x[gp2;t2]) * s[smpl;gp1;gp2] + (x[gp1;t1]-m[smpl;gp1]) * (l[gp2]***2.f + s[smpl;gp2;gp2]) ) 
        let eDnm = 2.f * ( (l[gp1]***2.f + s[smpl;gp1;gp1]) * (l[gp2]***2.f + s[smpl;gp2;gp2]) - s[smpl;gp1;gp2]***2.f )
        let e = exp(-eNom / eDnm)
        let Tnom = e * l[gp1] * l[gp2]

        let Tdnm = sqrt ( (l[gp1]***2.f + s[smpl;gp1;gp1]) * (l[gp2]***2.f + s[smpl;gp2;gp2]) - s[smpl;gp1;gp2]***2.f )

        let T = ElemExpr.ifThenElse gp1 gp2 (ElemExpr.scalar 0.0f) (Tnom / Tdnm)
        Expr.elements [nSmpls; nGps; nGps; nTrnSmpls; nTrnSmpls] T [mu; sigma; lengthscales; trnX]


    /// replace covariance matrix diagonal by specified variance
    let setCovDiag nSmpls nGps cov var =
        // inputs  cov[smpl, gp1, gp2]
        //         var[smpl, gp
        // output  cov[smpl, gp1, gp2]
        let smpl = ElemExpr.idx 0
        let gp1 = ElemExpr.idx 1
        let gp2 = ElemExpr.idx 2
        let c = ElemExpr.argElem<single> 0
        let v = ElemExpr.argElem<single> 1

        let cv = ElemExpr.ifThenElse gp1 gp2 (v[smpl; gp1]) (c[smpl; gp1; gp2])
        Expr.elements [nSmpls; nGps; nGps] cv [cov; var]


    ///Predicted mean and covariance from input mean and covariance.
    let pred pars (mu, sigma) =
        // mu:    input mean        [smpl, gp]
        // Sigma: input covariance  [smpl, gp1, gp2]
        let nSmpls    = (Expr.shapeOf mu).[0]
        let nTrnSmpls = pars.HyperPars.NTrnSmpls
        
        let nGps      = pars.HyperPars.NGPs
        let nOutput   = pars.HyperPars.NOutput
        // check inputs
        let mu    = mu    |> Expr.checkFinite "mu"
        let sigma = sigma |> Expr.checkFinite "sigma"
        // check parameters and gate gradients
        let lengthscales = 
            pars.Lengthscales
            |> gate pars.HyperPars.LengthscalesTrainable
            |> Expr.checkFinite "Lengthscales"
            |> Expr.replicateTo 0 nOutput 
        let trnX = 
            pars.TrnX
            |> gate pars.HyperPars.TrnXTrainable
            |> Expr.checkFinite "TrnX"
            |> Expr.replicateTo 0 nOutput
        // trnT [gp, trn_smpl]
        let trnT = 
            pars.TrnT
            |> gate pars.HyperPars.TrnTTrainable
            |> Expr.checkFinite "TrnT"
//            |> Expr.replicateTo 0 nOutput

        let trnSigma = 
            pars.TrnSigma
            |> gate pars.HyperPars.TrnSigmaTrainable
            |> Expr.checkFinite "TrnSigma"
//            |> Expr.replicateTo 0 nOutput

        // Kk [gp, trn_smpl1, trn_smpl2]
        let Kk = Kk nOutput nTrnSmpls lengthscales trnX trnSigma
        let Kk = Kk |> Expr.checkFinite "Kk"
        //let Kk = Kk |> Expr.dump "Kk"
        
        let KkInv = Expr.invert Kk
        let KkInv = KkInv |> Expr.checkFinite "Kk_inv"
        //let Kk_inv = Kk_inv |> Expr.dump "Kk_inv"
        
        // lk [smpl, gp, trn_smpl]
        let lk = lk nSmpls nOutput nTrnSmpls mu sigma lengthscales trnX
        let lk = lk |> Expr.checkFinite "lk"
        //let lk = lk |> Expr.dump "lk"
        
        // ([gp, trn_smpl1, trn_smpl2] .* [gp, trn_smpl])       
        // ==> beta [gp, trn_smpl]
        let beta = KkInv .* trnT
        //let beta = beta |> Expr.dump "beta"

        // ==> sum ( [smpl, gp, trn_smpl] * beta[1*, gp, trn_smpl], trn_smpl)
        // ==> pred_mean [smpl, gp]
        let predMean = lk * Expr.padLeft beta |> Expr.sumAxis 2
        let predMean = predMean |> Expr.checkFinite "pred_mean"
        //let predMean = pred_mean |> Expr.dump "pred_mean"
        let predMean = 
            if pars.HyperPars.CutOutsideRange then
                let xFirst = trnX.[*,0] |> Expr.reshape [SizeSpec.broadcastable;nOutput]|> Expr.broadcast [nSmpls;nOutput]
                let tFirst = trnT.[*,0] |> Expr.reshape [SizeSpec.broadcastable;nOutput]|> Expr.broadcast [nSmpls;nOutput]
                let xLast = trnX.[*,nTrnSmpls - 1] |> Expr.reshape [SizeSpec.broadcastable;nOutput]|> Expr.broadcast [nSmpls;nOutput]
                let tLast = trnT.[*,nTrnSmpls - 1] |> Expr.reshape [SizeSpec.broadcastable;nOutput]|> Expr.broadcast [nSmpls;nOutput]

                let predMean = Expr.ifThenElse (mu <<<< xFirst) tFirst predMean
                Expr.ifThenElse (mu >>>> xLast) tLast predMean
            else
                predMean

        // L[smpl, gp, trn_smpl1, trn_smpl2]
        let L = L nSmpls nOutput nTrnSmpls mu sigma lengthscales trnX

        // betaBetaT = beta .* beta.T
        // [gp, trn_smpl, 1] .* [gp, 1, trn_smpl] ==> [gp, trn_smpl, trn_smpl]
        // is equivalent to: [gp, trn_smpl, 1*] * [gp, 1*, trn_smpl]
        let betaBetaT = 
            Expr.reshape [nOutput; nTrnSmpls; SizeSpec.broadcastable] beta *
            Expr.reshape [nOutput; SizeSpec.broadcastable; nTrnSmpls] beta
        //let betaBetaT = betaBetaT |> Expr.dump "betaBetaT"

        // lkLkT = lk .* lk.T
        // [smpl, gp, trn_smpl, 1] .* [smpl, gp, 1, trn_smpl] ==> [smpl, gp, trn_smpl, trn_smpl]
        // is equivalent to: [smpl, gp, trn_smpl, 1*] * [smpl, gp, 1*, trn_smpl]
        let lkLkT =
            Expr.reshape [nSmpls; nOutput; nTrnSmpls; SizeSpec.broadcastable] lk *
            Expr.reshape [nSmpls; nOutput; SizeSpec.broadcastable; nTrnSmpls] lk
        //let lkLkT = lkLkT |> Expr.dump "lkLkT"

        // Tr( (Kk_inv - betaBetaT) .*  L )
        // ([1*, gp, trn_smpl1, trn_smpl2] - [1*, gp, trn_smpl, trn_smpl]) .* [smpl, gp, trn_smpl1, trn_smpl2]
        //   ==> Tr ([smpl, gp, trn_smpl1, trn_smpl2]) ==> [smpl, gp]
        let var1 = Expr.padLeft (KkInv - betaBetaT) .* L  |> Expr.trace
        //let var1 = var1 |> Expr.dump "var1"
        
        // Tr( lkLkT .* betaBeta.T ) 
        // [smpl, gp, trn_smpl, trn_smpl] .* [1*, gp, trn_smpl, trn_smpl] 
        //  ==> Tr ([smpl, gp, trn_smpl1, trn_smpl2]) ==> [smpl, gp]
        let var2 = lkLkT .* (Expr.padLeft betaBetaT) |> Expr.trace
        //let var2 = var2 |> Expr.dump "var2"

        let predVar = 1.0f - var1 - var2
        //let pred_var = pred_var |> Expr.dump "pred_var"

        // T[smpl, gp1, gp2, trn_smpl1, trn_smpl2]
        //let T = Told nSmpls nGps nTrnSmpls mu sigma !pars.Lengthscales !pars.TrnX
        let T = Tnew nSmpls nOutput nTrnSmpls mu sigma lengthscales trnX
        //let T = T |> Expr.dump "T"

        // calculate betaTbeta = beta.T .* T .* beta
        // beta[gp, trn_smpl]
        // T[smpl, gp1, gp2, trn_smpl1, trn_smpl2]
        // beta[gp1, trn_smpl1].T .* T[gp1,gp2, trn_smpl1, trn_smpl2] .* beta[gp2, trn_smpl2]
        // [1*, gp1, 1*, 1, trn_smpl1] .* [smpl, gp1, gp2, trn_smpl1, trn_smpl2] .* [1*, 1*, gp2, trn_smpl2, 1]
        // ==> [smpl, gp1, gp2, 1, 1]
        let bc = SizeSpec.broadcastable
        let one = SizeSpec.one
        let betaTbeta = 
            (Expr.reshape [bc; nOutput; bc; one; nTrnSmpls] beta) .* T .* 
            (Expr.reshape [bc; bc; nOutput; nTrnSmpls; one] beta)

        // [smpl, gp1, gp2, 1, 1] ==> [smpl, gp1, gp2]
        let betaTbeta =
            betaTbeta |> Expr.reshape [nSmpls; nOutput; nOutput]   
        //let betaTbeta = betaTbeta |> Expr.dump "betaTbeta"     

        // calculate m_k * m_l
        // [smpl, gp1, 1*] * [smpl, 1*, gp2]
        // ==> [smpl, gp1, gp2]
        let mkml = 
            (Expr.reshape [nSmpls; nOutput; bc] predMean) *
            (Expr.reshape [nSmpls; bc; nOutput] predMean)
        //let mkml = mkml |> Expr.dump "mkml"

        /// calculate pred_cov_without_var =  beta.T .* T .* beta - m_k * m_l
        let predCovWithoutVar = betaTbeta - mkml
        //let pred_cov_without_var = pred_cov_without_var |> Expr.dump "pred_cov_without_var"

        // replace diagonal in pred_cov_without_var by pred_var
        let predCov = setCovDiag nSmpls nOutput predCovWithoutVar predVar
        //let pred_cov = pred_cov |> Expr.dump "pred_cov"

        predMean, predCov
    let regularizationTerm pars q = 
        let trnT = pars.TrnT
        let trnX = pars.TrnT
        let trnTReg =
            if pars.HyperPars.TrnTTrainable then
                Regularization.lqRegularization trnT q
            else 
                Expr.zeroOfSameType trnT
        let trnXReg = 
            if pars.HyperPars.TrnTTrainable then
                Regularization.lqRegularization trnX q
            else 
                Expr.zeroOfSameType trnX
        trnTReg + trnXReg

/// Propagates a normal distribution through a weight matrix.
module WeightTransform =

    type HyperPars = {
        /// number of inputs
        NInput:         SizeSpecT 

        /// number of outputs
        NOutput:        SizeSpecT

        Trainable:      bool

        WeightsInit:    InitMethod
        BiasInit:       InitMethod
    }

    let defaultHyperPars = {
        NInput          = SizeSpec.fix 0
        NOutput         = SizeSpec.fix 0
        Trainable       = true
        WeightsInit     = FanOptimal
        BiasInit        = Const 0.0f
    }

    /// Weight layer parameters.
    type Pars = {
        /// weights [nOutput, nInput]
        Weights:        ExprT 
        /// bias [nOutput]
        Bias:           ExprT
        /// hyper-parameters
        HyperPars:      HyperPars
    }

    let pars (mb: ModelBuilder<_>) hp = {
        Weights   = mb.Param ("Weights", [hp.NOutput; hp.NInput], GPUtils.initVals hp.WeightsInit)
        Bias      = mb.Param ("Bias",    [hp.NOutput],            GPUtils.initVals hp.BiasInit)
        HyperPars = hp
    }

    /// Mean and variance after multiplication with the weight matrix.
    let transform pars (mu, sigma) =
        // [smpl,inp] .* [inp,out] + [out]
        // => [smpl,gp]
        let newMu = mu .* pars.Weights.T + pars.Bias
        // [1*,gp,inp] .* [smpl,inp,inp] => [smpl,gp,inp]
        // [smpl,gp,inp] .* [1*,inp,gp] => [smpl,gp,gp]
        // [1*,gp,inp] .* [smpl,inp,inp] .* [1*,inp,gp]
        // => [smpl,gp,gp]
        let nGps = pars.HyperPars.NOutput
        let nInput = pars.HyperPars.NInput
        let newSigma =  (Expr.reshape [SizeSpec.broadcastable; nGps; nInput] pars.Weights) .*
                        sigma .*
                        (Expr.reshape [SizeSpec.broadcastable; nInput; nGps] pars.Weights.T)
        newMu, newSigma

    let regularizationTerm pars (q:int) =
        let weights = pars.Weights
        if pars.HyperPars.Trainable then
            Regularization.lqRegularization weights q
        else 
            Expr.zeroOfSameType weights 
/// Layer that propagates its input normal distribution through a weight matrix and activation
/// functions described by GPs.
module GPActivationLayer = 

    type HyperPars = {
        WeightTransform: WeightTransform.HyperPars
        Activation:      GPActivation.HyperPars
    }

    let defaultHyperPars = {
        WeightTransform = WeightTransform.defaultHyperPars
        Activation      = GPActivation.defaultHyperPars
    }

    type Pars = {
        /// weight transform parameters
        WeightTransform: WeightTransform.Pars
        /// GP activation function parameters
        Activation:      GPActivation.Pars
        /// hyper-parameters
        HyperPars:       HyperPars
    }

    let pars (mb: ModelBuilder<_>) (hp: HyperPars) = 
        if hp.Activation.NOutput <> hp.WeightTransform.NOutput then
            failwith "number of Outputs must equal number of output units in weight transform"
        {
            WeightTransform = WeightTransform.pars (mb.Module "WeightTransform") hp.WeightTransform
            Activation = GPActivation.pars (mb.Module "Activation") hp.Activation
            HyperPars = hp
        }

    let regularizationTerm pars (q:int) =
        (GPActivation.regularizationTerm pars.Activation q) +
        (WeightTransform.regularizationTerm pars.WeightTransform q)


    /// Propagates the input normal distribution through a weight matrix and activation
    /// functions described by GPs.
    let pred (pars: Pars) (meanIn, covIn) = 
        let meanTf, covTf  = WeightTransform.transform pars.WeightTransform (meanIn, covIn)
        let meanAct,covAct = GPActivation.pred pars.Activation (meanTf, covTf)
        meanAct, covAct


module MeanOnlyGPLayer =
    /// Hyper-parameters
    /// Hyper-parameters
    type HyperPars = {
        /// number of Inputs
        NInput:                SizeSpecT
        
        /// number od Outputs
//        NOutput:                SizeSpecT
        
        /// number of GPs <= number of outputs
        NGPs:                   SizeSpecT

        /// number of training points for each GP
        NTrnSmpls:              SizeSpecT

        ///GP parameters (for all Gps in the layer)
        CutOutsideRange:        bool
        MeanFunction:       (ExprT -> ExprT)
        Monotonicity:       (single*int*single*single) option

        LengthscalesTrainable:  bool
        TrnXTrainable:          bool
        TrnTTrainable:          bool
        TrnSigmaTrainable:      bool
        WeightsTrainable:       bool

        LengthscalesInit:       InitMethod
        TrnXInit:               InitMethod
        TrnTInit:               InitMethod
        TrnSigmaInit:           InitMethod
        WeightsInit:            InitMethod
        BiasInit:               InitMethod
    }

    /// default hyper-parameters
    let defaultHyperPars = {
        NInput                = SizeSpec.fix 0
//        NOutput               = SizeSpec.fix 0
        NGPs                  = SizeSpec.fix 0
        NTrnSmpls             = SizeSpec.fix 10
        CutOutsideRange       = false
        MeanFunction          = (fun x -> Expr.zerosLike x)
        Monotonicity          = None
        LengthscalesTrainable = true
        TrnXTrainable         = true
        TrnTTrainable         = true
        TrnSigmaTrainable     = true
        WeightsTrainable      = true
        LengthscalesInit      = Const 0.4f
        TrnXInit              = Linspaced (-2.0f, 2.0f)
        TrnTInit              = Linspaced (-2.0f, 2.0f)
        TrnSigmaInit          = Const (sqrt 0.1f)
        WeightsInit     = FanOptimal
        BiasInit        = Const 0.0f
    }

    /// Parameter expressions.
    type Pars = {
        /// GP lengthscales: [gp]
        Lengthscales:       ExprT 
        /// x values of GP training samples:         [gp, trn_smpl]
        TrnX:               ExprT 
        /// target values of GP training samples:    [gp, trn_smpl]
        TrnT:               ExprT 
        /// standard deviation of GP target values:  [gp, trn_smpl]
        TrnSigma:           ExprT 
        /// weights [nOutput, nInput]
        Weights:        ExprT 
        /// bias [nOutput]
        Bias:           ExprT

        /// hyper-parameters
        HyperPars:          HyperPars
    }
    

    /// creates parameters
    let pars (mb: ModelBuilder<_>) hp = 
        {
        Lengthscales   = mb.Param ("Lengthscales", [hp.NGPs],               GPUtils.initVals hp.LengthscalesInit)
        TrnX           = mb.Param ("TrnX",         [hp.NGPs; hp.NTrnSmpls], GPUtils.initVals hp.TrnXInit)
        TrnT           = mb.Param ("TrnT",         [hp.NGPs; hp.NTrnSmpls], GPUtils.initVals hp.TrnTInit)
        TrnSigma       = mb.Param ("TrnSigma",     [hp.NGPs; hp.NTrnSmpls], GPUtils.initVals hp.TrnSigmaInit)
        Weights        = mb.Param ("Weights", [hp.NGPs; hp.NInput], GPUtils.initVals hp.WeightsInit)
        Bias           = mb.Param ("Bias",    [hp.NGPs],            GPUtils.initVals hp.BiasInit)    
        HyperPars      = hp
    }


    let covMat nGps nXSmpls nYSmpls lengthscales x y=
         let gp, xSmpl, ySmpl = ElemExpr.idx3   
         let lVec, xVec,yVec  = ElemExpr.arg3<single>
         let kse = (exp -((xVec[gp;xSmpl] - yVec[gp;ySmpl])***2.0f)/ (2.0f * lVec[gp]***2.0f))
         Expr.elements [nGps;nXSmpls;nYSmpls] kse [lengthscales;x;y]
    
    let pred pars input =
        
        let nSmpls    = (Expr.shapeOf input).[0]
//        let nGps      = pars.HyperPars.NGPs
        let nTrnSmpls = pars.HyperPars.NTrnSmpls
        let nOutput = pars.HyperPars.NGPs

        let lengthscales = 
            pars.Lengthscales
//            |> Expr.replicateTo 0 nOutput
            |> gate pars.HyperPars.LengthscalesTrainable
            |> Expr.checkFinite "Lengthscales"
        let trnX = 
            pars.TrnX
//            |> Expr.replicateTo 0 nOutput
            |> gate pars.HyperPars.TrnXTrainable
            |> Expr.checkFinite "TrnX"
        // trnT [gp, trn_smpl]
        let trnT = 
            pars.TrnT
//            |> Expr.replicateTo 0 nOutput
            |> gate pars.HyperPars.TrnTTrainable
            |> Expr.checkFinite "TrnT"
        let trnSigma = 
            pars.TrnSigma
//            |> Expr.replicateTo 0 nOutput
            |> gate pars.HyperPars.TrnSigmaTrainable
            |> Expr.checkFinite "TrnSigma"
        let input = Expr.checkFinite "Input" input
        let input = input .* pars.Weights.T + pars.Bias
        let K = (covMat nOutput nTrnSmpls nTrnSmpls lengthscales trnX trnX)  + Expr.diagMat trnSigma
        let KInv = Expr.invert K
        let KStarT = covMat nOutput nSmpls nTrnSmpls lengthscales input.T trnX
        let meanTrnX = pars.HyperPars.MeanFunction trnX
        let meanInput = pars.HyperPars.MeanFunction input
        let mean = meanInput + (KStarT .* KInv .* (trnT - meanTrnX)).T
        let mean = 
            if pars.HyperPars.CutOutsideRange then
                let xFirst = trnX.[*,0] |> Expr.reshape [SizeSpec.broadcastable;nOutput]|> Expr.broadcast [nSmpls;nOutput]
                let tFirst = trnT.[*,0] |> Expr.reshape [SizeSpec.broadcastable;nOutput]|> Expr.broadcast [nSmpls;nOutput]
                let xLast = trnX.[*,nTrnSmpls - 1] |> Expr.reshape [SizeSpec.broadcastable;nOutput]|> Expr.broadcast [nSmpls;nOutput]
                let tLast = trnT.[*,nTrnSmpls - 1] |> Expr.reshape [SizeSpec.broadcastable;nOutput]|> Expr.broadcast [nSmpls;nOutput]

                let mean = Expr.ifThenElse (input <<<< xFirst) tFirst mean
                Expr.ifThenElse (input >>>> xLast) tLast mean
            else
                mean
        mean

    let regularizationTerm pars (q:int) =
        let weights = pars.Weights
        if pars.HyperPars.WeightsTrainable then
            Regularization.lqRegularization weights q
        else 
            Expr.zeroOfSameType weights 