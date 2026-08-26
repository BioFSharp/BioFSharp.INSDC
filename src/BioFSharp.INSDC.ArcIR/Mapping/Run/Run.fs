namespace BioFSharp.INSDC.ArcIR

open BioFSharp.ArcIR
open BioFSharp.FileFormats.INSDC

/// Run -> an `Activity`: instrument + data-file sub-objects, an edge to its experiment. Explicit,
/// decompilation-decoupled: scalars, attributes, and identifiers become annotations.
[<RequireQualifiedAccess>]
module RunConversion =

    [<Literal>]
    let private source = "INSDC Run"

    /// Converts one Run record into its explicit ArcIR graph fragment.
    let convert (run: Run) : ConversionResult =
        let nodeId = Convert.entityId run

        let scalarAnns =
            [ Annotations.stringField source "Title" run.Title
              Annotations.stringField source "RunCenter" run.RunCenter
              (ArcValueConversion.ofNullableDateTime run.RunDate |> Option.map (Annotations.field source "RunDate")) ]
            |> List.choose id

        let attrAnns = Annotations.attributeAnnotations run.RunAttributes
        let idAnns, idEdges = Annotations.identifierAnnotations nodeId run.Identifiers

        let node =
            GraphBuilder.object' nodeId ArcObjectKind.Activity [ Vocabulary.DType.run ] [] (scalarAnns @ attrAnns @ idAnns)

        let instrument =
            if isNull run.Platform then [] else SubObjects.instrument nodeId run.Platform |> Option.toList

        let dataFiles =
            if isNull run.DataBlock then []
            else run.DataBlock.Files |> Seq.map (SubObjects.runFile nodeId) |> List.ofSeq

        let experimentPending =
            if isNull run.ExperimentRef then [] else [ Convert.pendingRef nodeId Vocabulary.Rel.hasExperiment run.ExperimentRef ]

        Convert.result node (instrument @ dataFiles @ Convert.institutionAgents nodeId run) (idEdges @ experimentPending)
