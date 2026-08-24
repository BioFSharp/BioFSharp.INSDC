namespace BioFSharp.INSDC.ArcIR

open Arc.Build
open BioFSharp.FileFormats.INSDC

/// Experiment -> an `Activity` (assay): instrument + library-protocol sub-objects, edges to study/sample.
/// Explicit, decompilation-decoupled: scalars, attributes, and identifiers become annotations.
[<RequireQualifiedAccess>]
module ExperimentConversion =

    [<Literal>]
    let private source = "INSDC Experiment"

    /// Converts one Experiment record into its explicit ArcIR graph fragment.
    let convert (experiment: Experiment) : ConversionResult =
        let nodeId = Convert.entityId experiment

        let scalarAnns =
            [ Annotations.stringField source "Title" experiment.Title
              (match experiment.Design with
               | null -> None
               | design -> Annotations.stringField source "DesignDescription" design.DesignDescription) ]
            |> List.choose id

        let attrAnns = Annotations.attributeAnnotations experiment.ExperimentAttributes
        let idAnns, idEdges = Annotations.identifierAnnotations nodeId experiment.Identifiers

        let node =
            ArcObject.create
                nodeId
                ArcObjectKind.Activity
                [ Vocabulary.DType.experiment; Vocabulary.DType.assay ]
                []
                (scalarAnns @ attrAnns @ idAnns)

        let instrument =
            if isNull experiment.Platform then [] else SubObjects.instrument nodeId experiment.Platform |> Option.toList

        let protocol =
            match experiment.Design with
            | null -> []
            | design when isNull design.LibraryDescriptor -> []
            | design -> [ SubObjects.protocol nodeId design.LibraryDescriptor ]

        let samplePending =
            match experiment.Design with
            | null -> []
            | design ->
                match design.SampleDescriptor with
                | null -> []
                | sd ->
                    let pooled =
                        if isNull sd.Pool then
                            []
                        else
                            (if isNull sd.Pool.DefaultMember then [] else [ Convert.pendingSampleRef nodeId sd.Pool.DefaultMember ])
                            @ (sd.Pool.Member |> Seq.map (Convert.pendingSampleRef nodeId) |> List.ofSeq)

                    Convert.pendingSampleRef nodeId sd :: pooled

        let studyPending =
            match experiment.StudyRef with
            | null -> []
            | sr -> [ Convert.pendingRef nodeId Vocabulary.Rel.hasStudy sr ]

        Convert.result node (instrument @ protocol @ Convert.institutionAgents nodeId experiment) (idEdges @ studyPending @ samplePending)
