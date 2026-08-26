namespace BioFSharp.ArcIR

/// A structural or referential ArcIR validation finding.
type ValidationIssue =
    /// A map key does not match the authoritative ID carried by its value.
    | IdentityKeyMismatch of container: Iri option * key: Iri * valueId: Iri
    /// An assertion references a term absent from the graph-level registry.
    | MissingTerm of owner: Iri * term: Iri
    /// A relation references an object absent from the graph.
    | MissingEndpoint of relation: Iri * endpoint: Iri
    /// An annotation references an evidence or source object absent from the graph.
    | MissingObjectReference of annotation: Iri * objectId: Iri
    /// One object repeats a semantic type in more than one independently addressed assertion.
    | DuplicateTypeAssertion of objectId: Iri * term: Iri * assertions: Iri list

/// Validation for normalized ArcIR graphs.
[<RequireQualifiedAccess>]
module Validation =

    let rec private valueTerms value =
        seq {
            match value with
            | ArcValue.Iri term -> yield term
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

    let private missingTerms owner terms ir =
        terms
        |> Seq.distinct
        |> Seq.choose (fun term -> if ir.Terms.ContainsKey term then None else Some(MissingTerm(owner, term)))

    let private annotationReferences (annotation: ArcAnnotation) ir =
        seq {
            for objectId in [ annotation.Evidence; annotation.Source ] |> List.choose id do
                if not (ir.Objects.ContainsKey objectId) then
                    yield MissingObjectReference(annotation.Id, objectId)
        }

    /// Returns every validation issue without mutating the graph.
    let validate (ir: ArcIR) =
        [
            for KeyValue(key, object') in ir.Objects do
                if key <> object'.Id then
                    IdentityKeyMismatch(None, key, object'.Id)

                for KeyValue(assertionKey, assertion) in object'.Types do
                    if assertionKey <> assertion.Id then
                        IdentityKeyMismatch(Some object'.Id, assertionKey, assertion.Id)
                    yield! missingTerms object'.Id [ assertion.Term ] ir

                for term, assertions in
                    object'.Types.Values
                    |> Seq.groupBy (fun assertion -> assertion.Term)
                    |> Seq.map (fun (term, assertions) -> term, List.ofSeq assertions) do
                    if assertions.Length > 1 then
                        DuplicateTypeAssertion(object'.Id, term, assertions |> List.map (fun assertion -> assertion.Id))

                for KeyValue(assertionKey, property) in object'.Properties do
                    if assertionKey <> property.Id then
                        IdentityKeyMismatch(Some object'.Id, assertionKey, property.Id)
                    yield! missingTerms object'.Id (propertyTerms property) ir
                    for annotation in property.Annotations.Values do
                        yield! annotationReferences annotation ir

                for KeyValue(annotationKey, annotation) in object'.Annotations do
                    if annotationKey <> annotation.Id then
                        IdentityKeyMismatch(Some object'.Id, annotationKey, annotation.Id)
                    yield! missingTerms object'.Id (annotationTerms annotation) ir
                    yield! annotationReferences annotation ir

            for KeyValue(key, relation) in ir.Relations do
                if key <> relation.Id then
                    IdentityKeyMismatch(None, key, relation.Id)
                if not (ir.Objects.ContainsKey relation.Subject) then
                    MissingEndpoint(relation.Id, relation.Subject)
                if not (ir.Objects.ContainsKey relation.Object) then
                    MissingEndpoint(relation.Id, relation.Object)
                yield! missingTerms relation.Id [ relation.Predicate ] ir

                for KeyValue(assertionKey, property) in relation.Properties do
                    if assertionKey <> property.Id then
                        IdentityKeyMismatch(Some relation.Id, assertionKey, property.Id)
                    yield! missingTerms relation.Id (propertyTerms property) ir

                for KeyValue(annotationKey, annotation) in relation.Annotations do
                    if annotationKey <> annotation.Id then
                        IdentityKeyMismatch(Some relation.Id, annotationKey, annotation.Id)
                    yield! missingTerms relation.Id (annotationTerms annotation) ir
                    yield! annotationReferences annotation ir
        ]
