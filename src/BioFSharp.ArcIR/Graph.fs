namespace BioFSharp.ArcIR

/// A conflict that prevents a lossless graph operation.
type GraphConflict =
    /// A term ID was already assigned a different definition.
    | TermConflict of id: Iri * existing: OntologyTerm * incoming: OntologyTerm
    /// An object ID was already assigned a different object by strict addition.
    | ObjectConflict of id: Iri * existing: ArcObject * incoming: ArcObject
    /// A relation ID was already assigned a different relation by strict addition.
    | RelationConflict of id: Iri * existing: ArcRelation * incoming: ArcRelation
    /// Two merge candidates assigned different structural kinds to one object ID.
    | ObjectKindConflict of id: Iri * existing: ArcObjectKind * incoming: ArcObjectKind
    /// An assertion ID was assigned incompatible values while merging an object or relation.
    | AssertionConflict of owner: Iri * assertion: Iri

/// Explicit, lossless operations over ArcIR graphs.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ArcIR =

    let private addStrict conflict key value values =
        match Map.tryFind key values with
        | None -> Ok(Map.add key value values)
        | Some existing when existing = value -> Ok values
        | Some existing -> Error(conflict key existing value)

    let private mergeAssertions owner (left: Map<Iri, 'value>) (right: Map<Iri, 'value>) =
        ((left, []), right)
        ||> Map.fold (fun (values, conflicts) id incoming ->
            match Map.tryFind id values with
            | None -> Map.add id incoming values, conflicts
            | Some existing when existing = incoming -> values, conflicts
            | Some _ -> values, AssertionConflict(owner, id) :: conflicts)

    let private mergeObject (existing: ArcObject) (incoming: ArcObject) =
        if existing.Kind <> incoming.Kind then
            Error [ ObjectKindConflict(existing.Id, existing.Kind, incoming.Kind) ]
        else
            let types, typeConflicts = mergeAssertions existing.Id existing.Types incoming.Types
            let properties, propertyConflicts = mergeAssertions existing.Id existing.Properties incoming.Properties
            let annotations, annotationConflicts = mergeAssertions existing.Id existing.Annotations incoming.Annotations
            let conflicts = typeConflicts @ propertyConflicts @ annotationConflicts

            if List.isEmpty conflicts then
                Ok { existing with Types = types; Properties = properties; Annotations = annotations }
            else
                Error conflicts

    /// Strictly adds a term definition, rejecting an incompatible duplicate ID.
    let addTerm id term ir =
        addStrict (fun key existing incoming -> TermConflict(key, existing, incoming)) id term ir.Terms
        |> Result.map (fun terms -> { ir with Terms = terms })

    /// Strictly adds an object, rejecting an incompatible duplicate ID.
    let addObject (object': ArcObject) ir =
        addStrict (fun key existing incoming -> ObjectConflict(key, existing, incoming)) object'.Id object' ir.Objects
        |> Result.map (fun objects -> { ir with Objects = objects })

    /// Strictly adds a relation, rejecting an incompatible duplicate ID.
    let addRelation (relation: ArcRelation) ir =
        addStrict (fun key existing incoming -> RelationConflict(key, existing, incoming)) relation.Id relation ir.Relations
        |> Result.map (fun relations -> { ir with Relations = relations })

    /// Intentionally inserts or replaces a term and returns the previous definition.
    let upsertTerm id term ir =
        { ir with Terms = Map.add id term ir.Terms }, Map.tryFind id ir.Terms

    /// Intentionally inserts or replaces an object and returns the previous object.
    let upsertObject (object': ArcObject) ir =
        { ir with Objects = Map.add object'.Id object' ir.Objects }, Map.tryFind object'.Id ir.Objects

    /// Intentionally inserts or replaces a relation and returns the previous relation.
    let upsertRelation (relation: ArcRelation) ir =
        { ir with Relations = Map.add relation.Id relation ir.Relations }, Map.tryFind relation.Id ir.Relations

    /// Merges two graphs without choosing a winner for incompatible definitions or assertions.
    let merge (left: ArcIR) (right: ArcIR) =
        let mutable terms = left.Terms
        let mutable objects = left.Objects
        let mutable relations = left.Relations
        let conflicts = ResizeArray<GraphConflict>()

        for KeyValue(id, incoming) in right.Terms do
            match Map.tryFind id terms with
            | None -> terms <- Map.add id incoming terms
            | Some existing when existing = incoming -> ()
            | Some existing -> conflicts.Add(TermConflict(id, existing, incoming))

        for KeyValue(id, incoming) in right.Objects do
            match Map.tryFind id objects with
            | None -> objects <- Map.add id incoming objects
            | Some existing ->
                match mergeObject existing incoming with
                | Ok merged -> objects <- Map.add id merged objects
                | Error errors -> errors |> List.iter conflicts.Add

        for KeyValue(id, incoming) in right.Relations do
            match Map.tryFind id relations with
            | None -> relations <- Map.add id incoming relations
            | Some existing when existing = incoming -> ()
            | Some existing -> conflicts.Add(RelationConflict(id, existing, incoming))

        if conflicts.Count = 0 then
            Ok { Terms = terms; Objects = objects; Relations = relations }
        else
            Error(List.ofSeq conflicts)

    /// Returns relations whose subject is `objectId`.
    let outgoing objectId ir =
        ir.Relations.Values |> Seq.filter (fun relation -> relation.Subject = objectId)

    /// Returns relations whose target is `objectId`.
    let incoming objectId ir =
        ir.Relations.Values |> Seq.filter (fun relation -> relation.Object = objectId)

    /// Returns objects having the requested coarse kind.
    let objectsByKind kind ir =
        ir.Objects.Values |> Seq.filter (fun object' -> object'.Kind = kind)

    /// Returns objects carrying the requested semantic type term.
    let objectsByType term ir =
        ir.Objects.Values
        |> Seq.filter (fun object' -> object'.Types.Values |> Seq.exists (fun assertion -> assertion.Term = term))
