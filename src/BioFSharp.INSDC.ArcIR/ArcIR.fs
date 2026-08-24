namespace Arc.Build

/// Operations over the current proof-of-concept ArcIR graph.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ArcIR =

    /// Adds an object by ID. An identical duplicate is ignored; a compatible
    /// duplicate merges types, properties, and annotations using the current
    /// proof-of-concept merge behavior.
    let addObject (object': ArcObject) (ir: ArcIR) =
        match ir.Objects.TryFind object'.Id with
        | None ->
            { ir with Objects = ir.Objects.Add(object'.Id, object') }

        | Some existing when existing = object' ->
            ir

        | Some existing ->
            let merged =
                {
                    existing with
                        DTypes = Set.union existing.DTypes object'.DTypes
                        Properties =
                            Map.fold
                                (fun acc key value -> Map.add key value acc)
                                existing.Properties
                                object'.Properties
                        Annotations =
                            existing.Annotations @ object'.Annotations
                }

            { ir with Objects = ir.Objects.Add(object'.Id, merged) }


    /// Adds a relation to the graph's relation set.
    let addRelation (relation: ArcRelation) (ir: ArcIR) =
        { ir with Relations = ir.Relations.Add relation }


    /// Adds each object in `objects` to `ir`.
    let addObjects objects ir =
        objects |> Seq.fold (fun acc o -> addObject o acc) ir


    /// Adds each relation in `relations` to `ir`.
    let addRelations relations ir =
        relations |> Seq.fold (fun acc r -> addRelation r acc) ir


    /// Merges the objects and relations of `right` into `left` using
    /// `addObject` and `addRelation`.
    let merge (left: ArcIR) (right: ArcIR) =
        left
        |> addObjects right.Objects.Values
        |> addRelations right.Relations


    /// Returns relations whose subject is `objectId`.
    let outgoing objectId ir =
        ir.Relations
        |> Seq.filter (fun r -> r.Subject = objectId)


    /// Returns relations whose target is `objectId`.
    let incoming objectId ir =
        ir.Relations
        |> Seq.filter (fun r -> r.Object = objectId)


    /// Returns objects having the requested coarse kind.
    let objectsByKind kind ir =
        ir.Objects.Values
        |> Seq.filter (fun o -> o.Kind = kind)


    /// Returns objects carrying the requested semantic type IRI.
    let objectsByDType dtype ir =
        ir.Objects.Values
        |> Seq.filter (fun o -> o.DTypes.Contains dtype)
