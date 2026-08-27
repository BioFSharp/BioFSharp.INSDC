namespace BioFSharp.INSDC.ArcIR

open BioFSharp.ArcIR
open BioFSharp.FileFormats.INSDC

/// Study -> a `Collection` node carrying its descriptor (title/abstract/type/project-id). Explicit,
/// decompilation-decoupled: descriptor fields, attributes, and identifiers become annotations.
[<RequireQualifiedAccess>]
module StudyConversion =

    [<Literal>]
    let private source = "INSDC Study"

    /// Converts one Study record into its explicit ArcIR graph fragment.
    let convert (study: Study) : ConversionResult =
        let nodeId = Convert.entityId study

        let descriptorAnns =
            match study.Descriptor with
            | null -> []
            | d ->
                let studyType =
                    match d.Study with
                    | null -> None
                    | st when not (System.String.IsNullOrWhiteSpace st.NewStudyType) ->
                        Some(Annotations.field source "StudyType" (ArcValue.String st.NewStudyType))
                    | st -> Some(Annotations.field source "StudyType" (ArcValueConversion.ofEnum st.ExistingStudyType))

                [ Annotations.stringTermField StructuralTerms.Study.title d.StudyTitle
                  Annotations.stringField source "StudyAbstract" d.StudyAbstract
                  Annotations.stringTermField StructuralTerms.Study.description d.StudyDescription
                  (if d.ProjectId.HasValue then
                       Some(Annotations.field source "ProjectId" (ArcValueConversion.ofInt64 d.ProjectId.Value))
                   else
                       None)
                  studyType ]
                |> List.choose id

        let attrAnns = Annotations.attributeAnnotations study.StudyAttributes
        let idAnns, idEdges = Annotations.identifierAnnotations nodeId study.Identifiers
        let accessionAnn = Annotations.stringTermField StructuralTerms.Study.archiveAccession study.Accession |> Option.toList

        let node =
            GraphBuilder.object' nodeId ArcObjectKind.Collection [ Vocabulary.DType.study ] [] (accessionAnn @ descriptorAnns @ attrAnns @ idAnns)

        Convert.result node (Convert.institutionAgents nodeId study) idEdges
