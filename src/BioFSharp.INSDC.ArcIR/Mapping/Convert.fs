namespace BioFSharp.INSDC.ArcIR

open BioFSharp.ArcIR
open BioFSharp.FileFormats.INSDC

/// Shared building blocks for the per-accession converters: id assignment, cross-entity pending
/// references, institution agents, and result assembly. Extracted so every entity converter in its own
/// folder can reuse them.
[<RequireQualifiedAccess>]
module Convert =

    type private InsdcObject = BioFSharp.FileFormats.INSDC.Object

    /// The source identity text for an entity: its accession, falling back to its alias.
    let entityId (o: #InsdcObject) : string =
        if not (System.String.IsNullOrWhiteSpace o.Accession) then o.Accession
        elif not (System.String.IsNullOrWhiteSpace o.Alias) then o.Alias
        else invalidArg "o" "INSDC record has neither accession nor alias; cannot assign an ArcIR identity."

    /// A cross-entity reference (`RefObject`) as a `PendingRelation` from `subject`, resolved later.
    let pendingRef (subject: string) (predicate: Iri) (refObj: #RefObject) : PendingRelation =
        { Subject = Identity.objectId subject
          Predicate = predicate
          TargetAccession = Option.ofObj refObj.Accession
          TargetRefname = Option.ofObj refObj.Refname
          TargetRefcenter = Option.ofObj refObj.Refcenter }

    /// A sample reference (`SAMPLE_DESCRIPTOR` / `SAMPLE_REF`) as a pending `hasSample` edge.
    /// INSDC references a sample by its SRA accession (e.g. `DRS039895`) while the BioSample
    /// node is keyed by its BioSample accession (`SAMD00064197`); the descriptor's nested
    /// `EXTERNAL_ID namespace="BioSample"` carries that key, so it is preferred as the edge
    /// target when present. Without it the edge would dangle to the (unloaded) SRA accession.
    let pendingSampleRef (subject: string) (refObj: #RefObject) : PendingRelation =
        let bioSampleAccession =
            if isNull refObj.Identifiers || isNull refObj.Identifiers.ExternalId then
                None
            else
                refObj.Identifiers.ExternalId
                |> Seq.tryPick (fun external ->
                    if
                        not (isNull (box external))
                        && System.String.Equals(external.Namespace, "BioSample", System.StringComparison.OrdinalIgnoreCase)
                        && not (System.String.IsNullOrWhiteSpace external.Value)
                    then
                        Some external.Value
                    else
                        None)

        let pending = pendingRef subject Vocabulary.Rel.hasSample refObj

        match bioSampleAccession with
        | Some accession -> { pending with TargetAccession = Some accession }
        | None -> pending

    /// A pending edge to a record identified by a bare accession string (for non-`RefObject` links).
    let pendingAccession (subject: string) (predicate: Iri) (accession: string) : PendingRelation option =
        if System.String.IsNullOrWhiteSpace accession then
            None
        else
            Some
                { Subject = Identity.objectId subject
                  Predicate = predicate
                  TargetAccession = Some accession
                  TargetRefname = None
                  TargetRefcenter = None }

    /// Center/broker institution strings as Agent(organization) sub-objects (deduped by name).
    let institutionAgents (nodeId: string) (o: #InsdcObject) : (ArcObject * ArcRelation) list =
        [ o.CenterName; o.BrokerName ] |> List.choose (SubObjects.organization nodeId)

    /// Assemble an entity node + its sub-object fragments + cross-entity pending refs into a result.
    let result (node: ArcObject) (subs: (ArcObject * ArcRelation) list) (pending: PendingRelation list) : ConversionResult =
        let subNodes, subEdges = List.unzip subs
        { Objects = node :: subNodes; Relations = subEdges; Pending = pending }
