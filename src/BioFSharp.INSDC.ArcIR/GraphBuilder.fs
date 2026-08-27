namespace BioFSharp.INSDC.ArcIR

open System
open System.Security.Cryptography
open System.Text
open BioFSharp.ArcIR

/// Deterministic identities used by this F1 adapter.
[<RequireQualifiedAccess>]
module Identity =

    let private sha256 (value: string) =
        use algorithm = SHA256.Create()
        algorithm.ComputeHash(Encoding.UTF8.GetBytes value)
        |> Array.map (fun b -> b.ToString("x2"))
        |> String.concat ""

    /// Converts an already-absolute adapter ID unchanged and maps a bare INSDC
    /// accession or alias into the adapter's stable URN namespace.
    let objectId (value: string) =
        match Iri.TryCreate value with
        | Some iri -> iri
        | None when not (String.IsNullOrWhiteSpace value) ->
            Iri.Create("urn:biofsharp:insdc:object:" + Uri.EscapeDataString(value.Trim()))
        | None -> invalidArg (nameof value) "Object identity must not be empty."

    /// Mints an assertion identity from its owner, role, and stable source discriminator.
    let assertion (owner: Iri) (role: string) (discriminator: string) =
        Iri.Create("urn:biofsharp:arcir:assertion:" + sha256 (owner.Value + "\n" + role + "\n" + discriminator))

    /// Mints a relation identity from its complete semantic slot.
    let relation (subject: Iri) (predicate: Iri) (objectId: Iri) =
        Iri.Create(
            "urn:biofsharp:arcir:relation:"
            + sha256 (subject.Value + "\n" + predicate.Value + "\n" + objectId.Value)
        )

/// Adapter-side construction of normalized core objects and relations.
[<RequireQualifiedAccess>]
module GraphBuilder =

    let private keyed what (values: 'value seq) (idOf: 'value -> Iri) =
        ((Map.empty, Set.empty), values)
        ||> Seq.fold (fun (result, seen) value ->
            let id = idOf value
            if Set.contains id seen then
                invalidArg what (sprintf "Duplicate generated identity: %s" id.Value)
            Map.add id value result, Set.add id seen)
        |> fst

    let private indexedByPredicate (properties: (Iri * ArcValue) list) =
        let counts = System.Collections.Generic.Dictionary<Iri, int>()
        properties
        |> List.map (fun (predicate, value) ->
            let occurrence =
                match counts.TryGetValue predicate with
                | true, count ->
                    counts.[predicate] <- count + 1
                    count + 1
                | _ ->
                    counts.[predicate] <- 1
                    1
            predicate, occurrence, value)

    /// Creates a normalized object from adapter terms and values. Assertion IDs
    /// depend on the object identity and semantic slot, never on the current value.
    let object' rawId kind (types: Iri list) (properties: (Iri * ArcValue) list) (annotations: ArcAnnotation list) =
        let id = Identity.objectId rawId

        let typeAssertions =
            types
            |> List.map (fun term ->
                let assertionId = Identity.assertion id "type" term.Value
                ArcTypeAssertion.create assertionId term)
            |> fun values -> keyed "types" values (fun (assertion: ArcTypeAssertion) -> assertion.Id)

        let propertyAssertions =
            indexedByPredicate properties
            |> List.map (fun (predicate, occurrence, value) ->
                let assertionId = Identity.assertion id "property" (predicate.Value + "\n" + string occurrence)
                ArcProperty.create assertionId predicate value Seq.empty)
            |> fun values -> keyed "properties" values (fun (assertion: ArcProperty) -> assertion.Id)

        let annotationAssertions =
            annotations
            |> List.map (fun annotation ->
                let assertionId = Identity.assertion id "annotation" annotation.Id.Value
                { annotation with Id = assertionId })
            |> fun values -> keyed "annotations" values (fun (annotation: ArcAnnotation) -> annotation.Id)

        { Id = id
          Kind = kind
          Types = typeAssertions
          Properties = propertyAssertions
          Annotations = annotationAssertions }

    /// Creates a normalized, deterministically identified relation from raw adapter object IDs.
    let relation (rawSubject: string) (predicate: Iri) (rawObject: string) (properties: (Iri * ArcValue) list) (annotations: ArcAnnotation list) =
        let subject = Identity.objectId rawSubject
        let objectId = Identity.objectId rawObject
        let id = Identity.relation subject predicate objectId

        let propertyAssertions =
            indexedByPredicate properties
            |> List.map (fun (propertyPredicate, occurrence, value) ->
                let assertionId = Identity.assertion id "property" (propertyPredicate.Value + "\n" + string occurrence)
                ArcProperty.create assertionId propertyPredicate value Seq.empty)

        let annotationAssertions =
            annotations
            |> List.map (fun annotation ->
                let assertionId = Identity.assertion id "annotation" annotation.Id.Value
                { annotation with Id = assertionId })

        ArcRelation.create id subject predicate objectId propertyAssertions annotationAssertions

    /// Creates a normalized relation from already-resolved object identities.
    let relationIri (subject: Iri) (predicate: Iri) (objectId: Iri) properties annotations =
        relation subject.Value predicate objectId.Value properties annotations

    let rec private valueTerms value =
        seq {
            match value with
            | ArcValue.Iri iri -> yield iri
            | ArcValue.List values ->
                for item in values do
                    yield! valueTerms item
            | _ -> ()
        }

    let private annotationTerms (annotation: ArcAnnotation) =
        seq {
            yield annotation.Property
            match annotation.Value with
            | AnnotationValue.Literal value -> yield! valueTerms value
            | AnnotationValue.Term term -> yield term
            | AnnotationValue.LiteralWithUnit(value, unit) ->
                yield! valueTerms value
                yield unit
            | AnnotationValue.TermWithUnit(value, unit) ->
                yield value
                yield unit
        }

    let private propertyTerms (property: ArcProperty) =
        seq {
            yield property.Predicate
            yield! valueTerms property.Value
            for annotation in property.Annotations.Values do
                yield! annotationTerms annotation
        }

    let private localName (iri: Iri) =
        let value = iri.Value
        let parts = value.Split([| '/'; '#' |], StringSplitOptions.RemoveEmptyEntries)
        if parts.Length = 0 then value else Uri.UnescapeDataString parts.[parts.Length - 1]

    let private definition (iri: Iri) =
        match StructuralTerms.tryDefinition iri with
        | Some definition -> definition
        | None ->
            let value: string = iri.Value
            let source =
                let fieldPrefix = "http://purl.org/arc/insdc/field/"
                if value.StartsWith(fieldPrefix, StringComparison.Ordinal) then
                    value.Substring(fieldPrefix.Length).Split('/').[0]
                    |> Uri.UnescapeDataString
                    |> Some
                elif value.StartsWith("http://purl.org/arc/insdc/attribute/", StringComparison.Ordinal) then
                    Some "INSDC attribute"
                elif value.StartsWith("http://purl.org/arc/insdc/identifier/", StringComparison.Ordinal) then
                    Some "INSDC identifier"
                else
                    Some "BioFSharp.INSDC.ArcIR"

            OntologyTerm.create (Some(localName iri)) source

    /// Builds the shared term registry required by a set of normalized objects and relations.
    let terms (objects: ArcObject seq) (relations: ArcRelation seq) =
        seq {
            for object' in objects do
                for assertion in object'.Types.Values do
                    yield assertion.Term
                for property in object'.Properties.Values do
                    yield! propertyTerms property
                for annotation in object'.Annotations.Values do
                    yield! annotationTerms annotation

            for relation in relations do
                yield relation.Predicate
                for property in relation.Properties.Values do
                    yield! propertyTerms property
                for annotation in relation.Annotations.Values do
                    yield! annotationTerms annotation
        }
        |> Seq.distinct
        |> Seq.map (fun iri -> iri, definition iri)
        |> Map.ofSeq

    /// Merges graph fragments or raises with the complete conflict set. This is
    /// the adapter's explicit policy for compatible shared sub-objects.
    let mergeOrFail left right =
        match ArcIR.merge left right with
        | Ok merged -> merged
        | Error conflicts -> invalidOp (sprintf "ArcIR graph merge conflicts: %A" conflicts)

    /// Adds objects and relations through explicit conflict-reporting merge semantics.
    let assemble (objects: ArcObject seq) (relations: ArcRelation seq) =
        let objects = List.ofSeq objects
        let relations = List.ofSeq relations
        let seed = { ArcIR.Empty with Terms = terms objects relations }

        let withObjects =
            (seed, objects)
            ||> List.fold (fun graph object' ->
                let fragment = { ArcIR.Empty with Objects = Map.ofList [ object'.Id, object' ] }
                mergeOrFail graph fragment)

        (withObjects, relations)
        ||> List.fold (fun graph relation ->
            let fragment = { ArcIR.Empty with Relations = Map.ofList [ relation.Id, relation ] }
            mergeOrFail graph fragment)
