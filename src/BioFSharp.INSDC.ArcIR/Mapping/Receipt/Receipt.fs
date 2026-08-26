namespace BioFSharp.INSDC.ArcIR

open BioFSharp.ArcIR
open BioFSharp.FileFormats.INSDC

/// Receipt -> an `Activity` acknowledging the submitted objects. Receipt is not an INSDC `Object` (no
/// accession/identifiers block), so its id and fields are assembled bespoke; its typed fields become
/// annotations and its per-entity id buckets become `acknowledges` edges.
[<RequireQualifiedAccess>]
module ReceiptConversion =

    [<Literal>]
    let private source = "INSDC Receipt"

    /// Converts one Receipt record into its explicit ArcIR graph fragment.
    let convert (receipt: Receipt) : ConversionResult =
        let nodeId =
            let bySubmission = if isNull receipt.Submission then null else receipt.Submission.Accession

            [ receipt.SubmissionFile; bySubmission ]
            |> List.tryFind (System.String.IsNullOrWhiteSpace >> not)
            |> Option.defaultValue "receipt"

        let annotations =
            [ Some(Annotations.field source "Success" (ArcValueConversion.ofBool receipt.Success))
              Some(Annotations.field source "ReceiptDate" (ArcValueConversion.ofDateTime receipt.ReceiptDate))
              Annotations.stringField source "SubmissionFile" receipt.SubmissionFile ]
            |> List.choose id

        let node =
            GraphBuilder.object' nodeId ArcObjectKind.Activity [ Vocabulary.DType.receipt ] [] annotations

        let ack (bucket: seq<Id>) =
            bucket |> Seq.choose (fun i -> Convert.pendingAccession nodeId Vocabulary.Rel.acknowledges i.Accession) |> List.ofSeq

        let submissionAck =
            if isNull receipt.Submission then []
            else Convert.pendingAccession nodeId Vocabulary.Rel.acknowledges receipt.Submission.Accession |> Option.toList

        let pending =
            [ receipt.Analysis; receipt.Experiment; receipt.Run; receipt.Sample; receipt.Study; receipt.Project; receipt.Dataset; receipt.Policy; receipt.Dac; receipt.Checklist; receipt.Samplegroup ]
            |> List.collect ack
            |> List.append submissionAck

        Convert.result node [] pending
