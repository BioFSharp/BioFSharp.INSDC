namespace Arc.Build

module ArcObject =

    let create id kind dtypes properties annotations =
        {
            Id = ArcId.Create id
            Kind = kind
            DTypes = Set.ofList dtypes
            Properties = Map.ofList properties
            Annotations = annotations
        }


module ArcRelation =

    let create subject predicate objectId properties annotations =
        {
            Id = None
            Subject = ArcId.Create subject
            Predicate = predicate
            Object = ArcId.Create objectId
            Properties = Map.ofList properties
            Annotations = annotations
        }

    let createWithId id subject predicate objectId properties annotations =
        {
            Id = Some (ArcId.Create id)
            Subject = ArcId.Create subject
            Predicate = predicate
            Object = ArcId.Create objectId
            Properties = Map.ofList properties
            Annotations = annotations
        }


module ArcAnnotation =

    let term iri name source =
        {
            Id = Iri.Create iri
            Name = name
            Source = source
        }

    let literal property value =
        {
            Property = property
            Value = AnnotationValue.Literal value
            Evidence = None
            Source = None
        }

    let literalWithUnit property value unit =
        {
            Property = property
            Value = AnnotationValue.LiteralWithUnit(value, unit)
            Evidence = None
            Source = None
        }

    let termValue property value =
        {
            Property = property
            Value = AnnotationValue.Term value
            Evidence = None
            Source = None
        }