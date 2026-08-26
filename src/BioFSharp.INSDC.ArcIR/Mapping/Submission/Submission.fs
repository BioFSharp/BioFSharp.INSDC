namespace BioFSharp.INSDC.ArcIR

open BioFSharp.ArcIR
open BioFSharp.FileFormats.INSDC

/// Submission -> a `Collection` node with its contacts as Agent sub-objects. Explicit,
/// decompilation-decoupled: scalars, attributes, and identifiers become annotations.
[<RequireQualifiedAccess>]
module SubmissionConversion =

    [<Literal>]
    let private source = "INSDC Submission"

    /// Converts one Submission record into its explicit ArcIR graph fragment.
    let convert (submission: Submission) : ConversionResult =
        let nodeId = Convert.entityId submission

        let scalarAnns =
            [ Annotations.stringField source "Title" submission.Title
              Annotations.stringField source "SubmissionComment" submission.SubmissionComment
              Annotations.stringField source "LabName" submission.LabName
              (ArcValueConversion.ofNullableDateTime submission.SubmissionDate |> Option.map (Annotations.field source "SubmissionDate")) ]
            |> List.choose id

        let attrAnns = Annotations.attributeAnnotations submission.SubmissionAttributes
        let idAnns, idEdges = Annotations.identifierAnnotations nodeId submission.Identifiers

        let node =
            GraphBuilder.object' nodeId ArcObjectKind.Collection [ Vocabulary.DType.submission ] [] (scalarAnns @ attrAnns @ idAnns)

        let contacts = submission.Contacts |> Seq.choose (SubObjects.submissionContact nodeId) |> List.ofSeq
        let lab = SubObjects.organization nodeId submission.LabName |> Option.toList

        Convert.result node (contacts @ lab @ Convert.institutionAgents nodeId submission) idEdges
