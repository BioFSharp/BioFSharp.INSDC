namespace BioFSharp.INSDC.ArcIR

open Arc.Build
open BioFSharp.FileFormats.INSDC

/// BioSample -> an `Observable` (input material) plus its organism/taxon sub-object. Explicit,
/// decompilation-decoupled: scalars, attributes, and identifiers become annotations.
[<RequireQualifiedAccess>]
module BioSampleConversion =

    [<Literal>]
    let private source = "INSDC BioSample"

    let convert (sample: BioSample) : ConversionResult =
        let nodeId = Convert.entityId sample

        let scalarAnns =
            [ Annotations.stringField source "Title" sample.Title
              Annotations.stringField source "Description" sample.Description ]
            |> List.choose id

        let attrAnns = Annotations.attributeAnnotations sample.SampleAttributes
        let idAnns, idEdges = Annotations.identifierAnnotations nodeId sample.Identifiers

        let node =
            ArcObject.create
                nodeId
                ArcObjectKind.Observable
                [ Vocabulary.DType.bioSample; Vocabulary.DType.sample ]
                []
                (scalarAnns @ attrAnns @ idAnns)

        let organism = if isNull sample.SampleName then [] else [ SubObjects.organism nodeId sample.SampleName ]

        Convert.result node (organism @ Convert.institutionAgents nodeId sample) idEdges
