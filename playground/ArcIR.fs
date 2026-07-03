namespace Arc.Build

module ArcIR =

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


    let addRelation (relation: ArcRelation) (ir: ArcIR) =
        { ir with Relations = ir.Relations.Add relation }


    let addObjects objects ir =
        objects |> Seq.fold (fun acc o -> addObject o acc) ir


    let addRelations relations ir =
        relations |> Seq.fold (fun acc r -> addRelation r acc) ir


    let merge (left: ArcIR) (right: ArcIR) =
        left
        |> addObjects right.Objects.Values
        |> addRelations right.Relations


    let outgoing objectId ir =
        ir.Relations
        |> Seq.filter (fun r -> r.Subject = objectId)


    let incoming objectId ir =
        ir.Relations
        |> Seq.filter (fun r -> r.Object = objectId)


    let objectsByKind kind ir =
        ir.Objects.Values
        |> Seq.filter (fun o -> o.Kind = kind)


    let objectsByDType dtype ir =
        ir.Objects.Values
        |> Seq.filter (fun o -> o.DTypes.Contains dtype)