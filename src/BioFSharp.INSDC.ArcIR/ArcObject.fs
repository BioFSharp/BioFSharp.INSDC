namespace Arc.Build

/// Construction helpers for graph objects.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ArcObject =

    /// Creates an object, validating `id` and converting the supplied
    /// collections to the current graph representation.
    let create id kind dtypes properties annotations =
        {
            Id = ArcId.Create id
            Kind = kind
            DTypes = Set.ofList dtypes
            Properties = Map.ofList properties
            Annotations = annotations
        }


/// Construction helpers for graph relations.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ArcRelation =

    /// Creates a relation without an explicit edge ID.
    let create subject predicate objectId properties annotations =
        {
            Id = None
            Subject = ArcId.Create subject
            Predicate = predicate
            Object = ArcId.Create objectId
            Properties = Map.ofList properties
            Annotations = annotations
        }

    /// Creates a relation with an explicit edge ID.
    let createWithId id subject predicate objectId properties annotations =
        {
            Id = Some (ArcId.Create id)
            Subject = ArcId.Create subject
            Predicate = predicate
            Object = ArcId.Create objectId
            Properties = Map.ofList properties
            Annotations = annotations
        }


/// Construction helpers for graph annotations and their terms.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ArcAnnotation =

    /// Creates an ontology term from an identifier, optional name, and source.
    let term iri name source =
        {
            Id = Iri.Create iri
            Name = name
            Source = source
        }

    /// Creates a literal annotation without evidence or source references.
    let literal property value =
        {
            Property = property
            Value = AnnotationValue.Literal value
            Evidence = None
            Source = None
        }

    /// Creates a literal annotation with a unit and no evidence or source references.
    let literalWithUnit property value unit =
        {
            Property = property
            Value = AnnotationValue.LiteralWithUnit(value, unit)
            Evidence = None
            Source = None
        }

    /// Creates a term-valued annotation without evidence or source references.
    let termValue property value =
        {
            Property = property
            Value = AnnotationValue.Term value
            Evidence = None
            Source = None
        }
