﻿namespace Nu
open System
open Prime
open OpenTK

/// An abstract data type for executing Effects.
type [<NoEquality; NoComparison>] Effector =
    private
        { ViewType : ViewType
          History : Slice list
          HistoryLength : int
          ProgressOffset : single
          EffectRate : int64
          EffectTime : int64
          EffectEnv : Definitions
          Chaos : System.Random }

[<RequireQualifiedAccess; CompilationRepresentation (CompilationRepresentationFlags.ModuleSuffix)>]
module Effector =

    let rec private selectNodes2 localTime playback (nodes : INode list) =
        match playback with
        | Once ->
            match nodes with
            | [] -> failwithumf ()
            | head :: [] -> (localTime, head, head)
            | head :: next :: tail ->
                if localTime > head.NodeLength then
                    match tail with
                    | _ :: _ -> selectNodes2 (localTime - head.NodeLength) playback (next :: tail)
                    | [] -> (head.NodeLength, head, next)
                else (localTime, head, next)
        | Loop ->
            let totalTime = List.fold (fun totalTime (node : INode) -> totalTime + node.NodeLength) 0L nodes 
            let moduloTime = localTime % totalTime
            selectNodes2 moduloTime Once nodes
        | Bounce ->
            let totalTime = List.fold (fun totalTime (node : INode) -> totalTime + node.NodeLength) 0L nodes 
            let moduloTime = localTime % totalTime
            let bouncing = localTime / totalTime % 2L = 1L
            let bounceTime = if bouncing then totalTime - moduloTime else moduloTime
            selectNodes2 bounceTime Once nodes

    let private selectNodes<'n when 'n :> INode> localTime playback (nodes : 'n list) =
        nodes |>
        List.map (fun node -> node :> INode) |>
        selectNodes2 localTime playback |>
        fun (fst, snd, thd) -> (fst, snd :?> 'n, thd :?> 'n)

    let inline private tween (scale : (^a * single) -> ^a) (value : ^a) (value2 : ^a) progress algorithm effector =
        match algorithm with
        | Constant ->
            value2
        | Linear ->
            value + scale (value2 - value, progress)
        | Random ->
            let rand = Rand.makeFromInt ^ int ^ (Math.Max (double progress, 0.000000001)) * double Int32.MaxValue
            let randValue = fst ^ Rand.nextSingle rand
            value + scale (value2 - value, randValue)
        | Chaos ->
            let chaosValue = single ^ effector.Chaos.NextDouble ()
            value + scale (value2 - value, chaosValue)
        | Ease ->
            let progressEaseIn = single ^ Math.Pow (Math.Sin (Math.PI * double progress * 0.5), 2.0)
            value + scale (value2 - value, progressEaseIn)
        | Sin ->
            let progressScaled = float progress * Math.PI * 2.0
            let progressSin = Math.Sin progressScaled
            value + scale (value2 - value, single progressSin)
        | Cos ->
            let progressScaled = float progress * Math.PI * 2.0
            let progressCos = Math.Cos progressScaled
            value + scale (value2 - value, single progressCos)

    let private applyLogic value value2 applicator =
        match applicator with
        | Or -> value || value2
        | Nor -> not value && not value2
        | Xor -> value <> value2
        | And -> value && value2
        | Nand -> not (value && value2)
        | LogicApplicator.Put -> value2

    let inline private applyTween scale ratio (value : ^a) (value2 : ^a) applicator =
        match applicator with
        | Sum -> value + value2
        | Diff -> value - value2
        | Scale -> scale (value, value2)
        | Ratio -> ratio (value, value2)
        | TweenApplicator.Put -> value2

    let private evalInset (celSize : Vector2i) celRun celCount stutter effector =
        let cel = int (effector.EffectTime / stutter) % celCount
        let celI = cel % celRun
        let celJ = cel / celRun
        let celX = celI * celSize.X
        let celY = celJ * celSize.Y
        let celPosition = Vector2 (single celX, single celY)
        let celSize = Vector2 (single celSize.X, single celSize.Y)
        Math.makeBounds celPosition celSize

    let evalArgument (argument : Argument) : Definition =
        match argument with
        | PassResource resource -> AsResource resource
        | PassAspect aspect -> AsAspect aspect
        | PassContent content -> AsContent ([], content)

    let rec evalResource resource effector : AssetTag =
        match resource with
        | ExpandResource definitionName ->
            match Map.tryFind definitionName effector.EffectEnv with
            | Some definition ->
                match definition with
                | AsResource resource -> evalResource resource effector
                | AsPlayback _
                | AsAspect _
                | AsContent _ ->
                    note ^ "Expected AsResource but received '" + Reflection.getTypeName definition + "'."
                    acvalue<AssetTag> Constants.Assets.DefaultImageValue
            | None ->
                note ^ "Could not find definition with name '" + definitionName + "'."
                acvalue<AssetTag> Constants.Assets.DefaultImageValue
        | Resource (packageName, assetName) -> { PackageName = packageName; AssetName = assetName }

    let rec private iterateArtifacts slice incrementers content effector =
        let effector = { effector with ProgressOffset = 0.0f }
        let slice = evalAspects slice incrementers effector
        (slice, evalContent slice content effector)

    and private cycleArtifacts slice incrementers content effector =
        let slice = evalAspects slice incrementers effector
        evalContent slice content effector

    and private evalProgress nodeTime nodeLength effector =
        let progress = if nodeLength = 0L then 1.0f else single nodeTime / single nodeLength
        let progress = progress + effector.ProgressOffset
        if progress > 1.0f then progress - 1.0f else progress

    and private evalAspect slice aspect effector =
        match aspect with
        | ExpandAspect definitionName ->
            match Map.tryFind definitionName effector.EffectEnv with
            | Some definition ->
                match definition with
                | AsAspect aspect -> evalAspect slice aspect effector
                | AsPlayback _
                | AsResource _
                | AsContent _ -> note ^ "Expected AsAspect but received '" + Reflection.getTypeName definition + "'."; slice
            | None -> note ^ "Could not find definition with name '" + definitionName + "'."; slice
        | Visible (applicator, playback, nodes) ->
            let (_, _, node) = selectNodes effector.EffectTime playback nodes
            let applied = applyLogic slice.Visible node.LogicValue applicator
            { slice with Visible = applied }
        | Enabled (applicator, playback, nodes) ->
            let (_, _, node) = selectNodes effector.EffectTime playback nodes
            let applied = applyLogic slice.Enabled node.LogicValue applicator
            { slice with Enabled = applied }
        | Position (applicator, algorithm, playback, nodes) ->
            let (nodeTime, node, node2) = selectNodes effector.EffectTime playback nodes
            let progress = evalProgress nodeTime node.TweenLength effector
            let tweened = tween Vector2.op_Multiply node.TweenValue node2.TweenValue progress algorithm effector
            let applied = applyTween Vector2.Multiply Vector2.Divide slice.Position tweened applicator
            { slice with Position = applied }
        | Translation (applicator, algorithm, playback, nodes) ->
            let (nodeTime, node, node2) = selectNodes effector.EffectTime playback nodes
            let progress = evalProgress nodeTime node.TweenLength effector
            let tweened = tween Vector2.op_Multiply node.TweenValue node2.TweenValue progress algorithm effector
            let oriented = Vector2.Transform (tweened, Quaternion.FromAxisAngle (Vector3.UnitZ, slice.Rotation))
            let applied = applyTween Vector2.Multiply Vector2.Divide slice.Position oriented applicator
            { slice with Position = applied }
        | Size (applicator, algorithm, playback, nodes) ->
            let (nodeTime, node, node2) = selectNodes effector.EffectTime playback nodes
            let progress = evalProgress nodeTime node.TweenLength effector
            let tweened = tween Vector2.op_Multiply node.TweenValue node2.TweenValue progress algorithm effector
            let applied = applyTween Vector2.Multiply Vector2.Divide slice.Size tweened applicator
            { slice with Size = applied }
        | Rotation (applicator, algorithm, playback, nodes) ->
            let (nodeTime, node, node2) = selectNodes effector.EffectTime playback nodes
            let progress = evalProgress nodeTime node.TweenLength effector
            let tweened = tween (fun (x, y) -> x * y) node.TweenValue node2.TweenValue progress algorithm effector
            let applied = applyTween (fun (x, y) -> x * y) (fun (x, y) -> x / y) slice.Rotation tweened applicator
            { slice with Rotation = applied }
        | Depth (applicator, algorithm, playback, nodes) ->
            let (nodeTime, node, node2) = selectNodes effector.EffectTime playback nodes
            let progress = evalProgress nodeTime node.TweenLength effector
            let tweened = tween (fun (x, y) -> x * y) node.TweenValue node2.TweenValue progress algorithm effector
            let applied = applyTween (fun (x, y) -> x * y) (fun (x, y) -> x / y) slice.Depth tweened applicator
            { slice with Depth = applied }
        | Offset (applicator, algorithm, playback, nodes) ->
            let (nodeTime, node, node2) = selectNodes effector.EffectTime playback nodes
            let progress = evalProgress nodeTime node.TweenLength effector
            let tweened = tween Vector2.op_Multiply node.TweenValue node2.TweenValue progress algorithm effector
            let applied = applyTween Vector2.Multiply Vector2.Divide slice.Size tweened applicator
            { slice with Offset = applied }
        | Color (applicator, algorithm, playback, nodes) ->
            let (nodeTime, node, node2) = selectNodes effector.EffectTime playback nodes
            let progress = evalProgress nodeTime node.TweenLength effector
            let tweened = tween Vector4.op_Multiply node.TweenValue node2.TweenValue progress algorithm effector
            let applied = applyTween Vector4.Multiply Vector4.Divide slice.Color tweened applicator
            { slice with Color = applied }

    and private evalAspects slice aspects effector =
        List.fold (fun slice aspect -> evalAspect slice aspect effector) slice aspects

    and private evalStaticSprite slice resource aspects content effector =

        // pull image from resource
        let image = evalResource resource effector

        // eval aspects
        let slice = evalAspects slice aspects effector

        // build sprite artifacts
        let spriteArtifacts =
            if slice.Visible then
                [RenderArtifact
                    [LayerableDescriptor
                        { Depth = slice.Depth
                          LayeredDescriptor =
                            SpriteDescriptor 
                                { Position = slice.Position
                                  Size = slice.Size
                                  Rotation = slice.Rotation
                                  Offset = slice.Offset
                                  OptInset = None
                                  Image = image
                                  ViewType = effector.ViewType
                                  Color = slice.Color }}]]
            else []

        // build implicitly mounted content
        let mountedArtifacts = evalContent slice content effector

        // return artifacts
        mountedArtifacts @ spriteArtifacts

    and private evalAnimatedSprite slice resource celSize celRun celCount stutter aspects content effector =

        // pull image from resource
        let image = evalResource resource effector

        // eval aspects
        let slice = evalAspects slice aspects effector

        // eval inset
        let inset = evalInset celSize celRun celCount stutter effector

        // build animated sprite artifacts
        let animatedSpriteArtifacts =
            if slice.Visible then
                [RenderArtifact
                    [LayerableDescriptor
                        { Depth = slice.Depth
                          LayeredDescriptor =
                            SpriteDescriptor 
                                { Position = slice.Position
                                  Size = slice.Size
                                  Rotation = slice.Rotation
                                  Offset = slice.Offset
                                  OptInset = Some inset
                                  Image = image
                                  ViewType = effector.ViewType
                                  Color = slice.Color }}]]
            else []

        // build implicitly mounted content
        let mountedArtifacts = evalContent slice content effector

        // return artifacts
        mountedArtifacts @ animatedSpriteArtifacts

    and private evalComposite slice contents effector =
        evalContents slice contents effector

    and private evalContent slice content effector =
        match content with
        | ExpandContent (definitionName, arguments) ->
            match Map.tryFind definitionName effector.EffectEnv with
            | Some definition ->
                match definition with
                | AsContent (parameters, content) ->
                    let localDefinitions = List.map evalArgument arguments
                    match (try List.zip parameters localDefinitions |> Some with _ -> None) with
                    | Some localDefinitionEntries ->
                        let effector = { effector with EffectEnv = Map.addMany localDefinitionEntries effector.EffectEnv }
                        evalContent slice content effector
                    | None -> note "Wrong number of arguments provided to ExpandContent."; []
                | AsPlayback _
                | AsResource _
                | AsAspect _ -> note ^ "Expected AsContent but received '" + Reflection.getTypeName definition + "'."; []
            | None -> note ^ "Could not find definition with name '" + definitionName + "'."; []
        | StaticSprite (resource, aspects, content) ->
            evalStaticSprite slice resource aspects content effector
        | AnimatedSprite (resource, celSize, celRun, celCount, stutter, aspects, content) ->
            evalAnimatedSprite slice resource celSize celRun celCount stutter aspects content effector
        | PhysicsShape (label, bodyShape, collisionCategories, collisionMask, aspects, content) ->
            ignore (label, bodyShape, collisionCategories, collisionMask, aspects, content); [] // TODO: implement
        | Tag (name, metadata) ->
            [TagArtifact (name, metadata, slice)]
        | Mount (Shift shift, aspects, content) ->
            let slice = { slice with Depth = slice.Depth + shift }
            let slice = evalAspects slice aspects effector
            evalContent slice content effector
        | Repeat (Shift shift, repetition, incrementers, content) ->
            let slice = { slice with Depth = slice.Depth + shift }
            match repetition with
            | Iterate count ->
                List.fold
                    (fun (slice, artifacts) _ ->
                        let (slice, artifacts') = iterateArtifacts slice incrementers content effector
                        (slice, artifacts @ artifacts'))
                    (slice, [])
                    [0 .. count - 1] |>
                snd
            | Cycle count ->
                List.fold
                    (fun artifacts i ->
                        let effector = { effector with ProgressOffset = 1.0f / single count * single i }
                        let artifacts' = cycleArtifacts slice incrementers content effector
                        artifacts @ artifacts')
                    [] [0 .. count - 1]
        | Emit (Shift shift, Rate rate, emitterAspects, aspects, content) ->
            let artifacts =
                List.foldi
                    (fun i artifacts (slice : Slice) ->
                        let timePassed = int64 i * effector.EffectRate
                        let slice = { slice with Depth = slice.Depth + shift }
                        let baseEffector = { effector with EffectTime = effector.EffectTime - timePassed }
                        let slice = evalAspects slice emitterAspects baseEffector
                        let emitCountLastFrame = single (effector.EffectTime - timePassed - effector.EffectRate) * rate
                        let emitCountThisFrame = single (effector.EffectTime - timePassed) * rate
                        let emitCount = int emitCountThisFrame - int emitCountLastFrame
                        let effector =
                            let history = effector.History
                                (*match content with
                                | Emit _ ->
                                    List.mapi
                                        (fun i slice ->
                                            let timePassed = int64 i * baseEffector.EffectRate
                                            let baseEffector = { effector with EffectTime = baseEffector.EffectTime - timePassed }
                                            evalAspects slice aspects baseEffector)
                                        baseEffector.History
                                | _ -> effector.History*)
                            { effector with
                                History = history
                                EffectTime = timePassed }
                        let artifacts' =
                            List.fold
                                (fun artifacts' _ ->
                                    let slice = evalAspects slice aspects effector
                                    let artifacts'' =
                                        if slice.Enabled
                                        then evalContent slice content effector
                                        else []
                                    artifacts'' @ artifacts')
                                []
                                [0 .. emitCount - 1]
                        artifacts' @ artifacts)
                    []
                    effector.History
            artifacts // temp for debuggability
        | Bone -> [] // TODO: implement
        | Composite (Shift shift, contents) ->
            let slice = { slice with Depth = slice.Depth + shift }
            evalComposite slice contents effector
        | End -> []

    and private evalContents slice contents effector =
        List.fold
            (fun artifacts content ->
                let artifacts' = evalContent slice content effector
                artifacts' @ artifacts)
            []
            contents

    let eval slice (effect : Effect) (effector : Effector) : EffectArtifact list =
        let localTime =
            match effect.OptLifetime with
            | Some lifetime -> effector.EffectTime % lifetime
            | None -> effector.EffectTime
        let effector =
            { effector with
                EffectEnv = Map.concat effector.EffectEnv effect.Definitions
                EffectTime = localTime }
        evalContent slice effect.Content effector

    let make viewType history tickRate tickTime globalEnv = 
        { ViewType = viewType
          History = history
          HistoryLength = List.length history
          ProgressOffset = 0.0f
          EffectRate = tickRate
          EffectTime = tickTime
          EffectEnv = globalEnv
          Chaos = System.Random () }