namespace BioFSharp.INSDC.ArcIR

open Arc.Build
open BioFSharp.FileFormats.INSDC

/// BioProject -> ArcIR, authored explicitly (object integrity preserved) and decoupled from the
/// structural-ontology decompilation: scalar fields, attributes, and identifiers become annotations
/// (composites folded, not shredded into per-leaf annotations); institutions become Agent nodes; related
/// projects and modelled external identifiers become edges.
[<RequireQualifiedAccess>]
module BioProjectConversion =

    [<Literal>]
    let private source = "INSDC BioProject"

    /// Converts one BioProject record into its explicit ArcIR graph fragment.
    let convert (project: BioProject) : ConversionResult =
        let nodeId = Convert.entityId project

        let scalarAnnotations =
            [ Annotations.stringField source "Alias" project.Alias
              Annotations.stringField source "Name" project.Name
              Annotations.stringField source "Title" project.Title
              Annotations.stringField source "Description" project.Description
              (ArcValueConversion.ofNullableDateTime project.FirstPublic
               |> Option.map (Annotations.field source "FirstPublic")) ]
            |> List.choose id

        let attrAnns = Annotations.attributeAnnotations project.ProjectAttributes
        let idAnns, idEdges = Annotations.identifierAnnotations nodeId project.Identifiers

        let node =
            ArcObject.create
                nodeId
                ArcObjectKind.Collection
                [ Vocabulary.DType.bioProject; Vocabulary.DType.investigation ]
                []
                (scalarAnnotations @ attrAnns @ idAnns)

        let relatedEdges =
            project.RelatedProjects
            |> Seq.collect (fun rp ->
                [ (if isNull rp.ParentProject then None else Convert.pendingAccession nodeId Vocabulary.Rel.hasParentProject rp.ParentProject.Accession)
                  (if isNull rp.ChildProject then None else Convert.pendingAccession nodeId Vocabulary.Rel.hasChildProject rp.ChildProject.Accession)
                  (if isNull rp.PeerProject then None else Convert.pendingAccession nodeId Vocabulary.Rel.hasPeerProject rp.PeerProject.Accession) ]
                |> List.choose id)
            |> List.ofSeq

        Convert.result node (Convert.institutionAgents nodeId project) (idEdges @ relatedEdges)
