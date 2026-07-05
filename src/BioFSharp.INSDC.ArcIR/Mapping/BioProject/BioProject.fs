namespace BioFSharp.INSDC.ArcIR

open Arc.Build
open BioFSharp.FileFormats.INSDC

/// BioProject -> ArcIR, authored explicitly (object integrity preserved) and decoupled from the
/// structural-ontology decompilation: scalar fields, attributes, and identifiers become annotations
/// (composites folded, not shredded into per-leaf annotations); institutions become Agent nodes; related
/// projects and modelled external identifiers become edges. This is the template for the per-accession
/// converters; the others still use the flat decompilation overlay for now.
[<RequireQualifiedAccess>]
module BioProjectConversion =

    [<Literal>]
    let private source = "INSDC BioProject"

    let private entityId (project: BioProject) : string =
        if not (System.String.IsNullOrWhiteSpace project.Accession) then project.Accession
        elif not (System.String.IsNullOrWhiteSpace project.Alias) then project.Alias
        else invalidArg "project" "BioProject has neither accession nor alias; cannot assign an ArcId."

    let private pendingAccession (subject: string) (predicate: Iri) (accession: string) : PendingRelation option =
        if System.String.IsNullOrWhiteSpace accession then
            None
        else
            Some
                { Subject = ArcId.Create subject
                  Predicate = predicate
                  TargetAccession = Some accession
                  TargetRefname = None
                  TargetRefcenter = None }

    let convert (project: BioProject) : ConversionResult =
        let nodeId = entityId project

        let scalarAnnotations =
            [ "Alias", project.Alias
              "Name", project.Name
              "Title", project.Title
              "Description", project.Description ]
            |> List.choose (fun (key, value) ->
                if System.String.IsNullOrWhiteSpace value then
                    None
                else
                    Some(Annotations.field source key (ArcValue.String value)))

        let dateAnnotations =
            ArcValueConversion.ofNullableDateTime project.FirstPublic
            |> Option.map (Annotations.field source "FirstPublic")
            |> Option.toList

        let attrAnns = Annotations.attributeAnnotations project.ProjectAttributes
        let idAnns, idEdges = Annotations.identifierAnnotations nodeId project.Identifiers

        let annotations = scalarAnnotations @ dateAnnotations @ attrAnns @ idAnns

        let node =
            ArcObject.create
                nodeId
                ArcObjectKind.Collection
                [ Vocabulary.DType.bioProject; Vocabulary.DType.investigation ]
                []
                annotations

        let agents =
            [ project.CenterName; project.BrokerName ]
            |> List.choose (SubObjects.organization nodeId)

        let relatedEdges =
            project.RelatedProjects
            |> Seq.collect (fun rp ->
                [ (if isNull rp.ParentProject then None else pendingAccession nodeId Vocabulary.Rel.hasParentProject rp.ParentProject.Accession)
                  (if isNull rp.ChildProject then None else pendingAccession nodeId Vocabulary.Rel.hasChildProject rp.ChildProject.Accession)
                  (if isNull rp.PeerProject then None else pendingAccession nodeId Vocabulary.Rel.hasPeerProject rp.PeerProject.Accession) ]
                |> List.choose id)
            |> List.ofSeq

        let subNodes, subEdges = List.unzip agents

        { Objects = node :: subNodes
          Relations = subEdges
          Pending = idEdges @ relatedEdges }
