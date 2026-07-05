namespace BioFSharp.INSDC.ArcIR

open Arc.Build
open BioFSharp.FileFormats.INSDC

/// Analysis -> an `Activity` (the reference hub): edges to study/sample/experiment/run/analysis, data
/// files. Explicit, decompilation-decoupled: scalars, attributes, and identifiers become annotations.
[<RequireQualifiedAccess>]
module AnalysisConversion =

    [<Literal>]
    let private source = "INSDC Analysis"

    let convert (analysis: Analysis) : ConversionResult =
        let nodeId = Convert.entityId analysis

        let analysisType =
            match analysis.AnalysisProperty with
            | null -> None
            | at ->
                at.GetType().GetProperties()
                |> Array.tryPick (fun p -> if isNull (p.GetValue at) then None else Some p.Name)
                |> Option.map (fun name -> Annotations.field source "AnalysisType" (ArcValue.String name))

        let scalarAnns =
            [ Annotations.stringField source "Title" analysis.Title
              Annotations.stringField source "Description" analysis.Description
              Annotations.stringField source "AnalysisCenter" analysis.AnalysisCenter
              (ArcValueConversion.ofNullableDateTime analysis.AnalysisDate |> Option.map (Annotations.field source "AnalysisDate"))
              analysisType ]
            |> List.choose id

        let attrAnns = Annotations.attributeAnnotations analysis.AnalysisAttributes
        let idAnns, idEdges = Annotations.identifierAnnotations nodeId analysis.Identifiers

        let node =
            ArcObject.create nodeId ArcObjectKind.Activity [ Vocabulary.DType.analysis ] [] (scalarAnns @ attrAnns @ idAnns)

        let dataFiles = analysis.Files |> Seq.map (SubObjects.analysisFile nodeId) |> List.ofSeq
        let centerAgent = SubObjects.organization nodeId analysis.AnalysisCenter |> Option.toList

        let refEdges =
            [ (if isNull analysis.StudyRef then [] else [ Convert.pendingRef nodeId Vocabulary.Rel.hasStudy analysis.StudyRef ])
              (analysis.SampleRef |> Seq.map (Convert.pendingSampleRef nodeId) |> List.ofSeq)
              (analysis.ExperimentRef |> Seq.map (Convert.pendingRef nodeId Vocabulary.Rel.hasExperiment) |> List.ofSeq)
              (analysis.RunRef |> Seq.map (Convert.pendingRef nodeId Vocabulary.Rel.hasRun) |> List.ofSeq)
              (analysis.AnalysisRef |> Seq.map (Convert.pendingRef nodeId Vocabulary.Rel.hasAnalysis) |> List.ofSeq) ]
            |> List.concat

        Convert.result node (dataFiles @ centerAgent @ Convert.institutionAgents nodeId analysis) (idEdges @ refEdges)
