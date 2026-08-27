namespace BioFSharp.ArcIR.Tests

open System
open System.IO
open System.Text
open BioFSharp.ArcIR

module internal Phase3Fixtures =

    type LocationIds =
        { Term: Iri
          Object: Iri
          TypeAssertion: Iri
          Property: Iri
          PropertyAnnotation: Iri
          ObjectAnnotation: Iri
          Relation: Iri
          RelationProperty: Iri
          RelationPropertyAnnotation: Iri
          RelationAnnotation: Iri }

    let iri value = Iri.Create value

    let expectOk result =
        match result with
        | Ok value -> value
        | Error errors -> failwithf "Expected success, got %A" errors

    let expectErrorCode expected (result: Result<'T, PersistenceError list>) =
        match result with
        | Ok value -> failwithf "Expected error '%s', got success %A" expected value
        | Error errors ->
            if errors |> List.exists (fun error -> error.Code = expected) |> not then
                failwithf "Expected error '%s', got %A" expected errors

    let bytes graph = ArcIRJson.writeBytes graph |> expectOk

    let readBytes (bytes: byte array) =
        use stream = new MemoryStream(bytes, false)
        ArcIRJson.read stream |> expectOk

    let utf8 (value: string) = UTF8Encoding(false).GetBytes value

    let fixturePath name =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "arcir", name)

    let schemaPath =
        Path.Combine(AppContext.BaseDirectory, "schema", "arcir-1.0.schema.json")

    let simpleState value =
        let predicate = iri "urn:example:predicate"
        let objectId = iri "urn:example:object"
        let assertionId = iri "urn:example:assertion"
        let property = ArcProperty.create assertionId predicate (ArcValue.String value) Seq.empty
        let object' = ArcObject.create objectId ArcObjectKind.Collection Seq.empty [ property ] Seq.empty

        { Terms = Map.ofList [ predicate, OntologyTerm.create (Some "Predicate") (Some "fixture") ]
          Objects = Map.ofList [ objectId, object' ]
          Relations = Map.empty }

    let simpleLocation =
        ArcJsonLocation.PropertyValue(iri "urn:example:object", iri "urn:example:assertion")

    let specialFloatState value =
        let predicate = iri "urn:phase3:float-predicate"
        let objectId = iri "urn:phase3:float-object"
        let assertionId = iri "urn:phase3:float-assertion"
        let property = ArcProperty.create assertionId predicate (ArcValue.Float value) Seq.empty
        let object' = ArcObject.create objectId ArcObjectKind.Collection Seq.empty [ property ] Seq.empty

        { ArcIR.Empty with Objects = Map.ofList [ objectId, object' ] }

    let comprehensiveGraph, locationIds =
        let typeTerm = iri "urn:phase3:term:type"
        let predicate = iri "urn:phase3:term:predicate"
        let annotationProperty = iri "urn:phase3:term:annotation-property"
        let unitTerm = iri "urn:phase3:term:unit"
        let valueTerm = iri "urn:phase3:term:value"
        let relationPredicate = iri "urn:phase3:term:relation-predicate"
        let trickyTerm = iri "https://example.org/a/~snow/雪#part%2F"

        let mainObjectId = iri "urn:phase3:object:observable"
        let instrumentId = iri "urn:phase3:object:instrument"
        let resourceId = iri "urn:phase3:object:resource"
        let activityId = iri "urn:phase3:object:activity"
        let agentId = iri "urn:phase3:object:agent"
        let roleId = iri "urn:phase3:object:role"
        let recipeId = iri "urn:phase3:object:recipe"
        let collectionId = iri "urn:phase3:object:collection"
        let selectorId = iri "urn:phase3:object:selector"

        let typeAssertionId = iri "urn:phase3:assertion:type"
        let stringPropertyId = iri "urn:phase3:assertion:string"
        let integerPropertyId = iri "urn:phase3:assertion:integer"
        let floatPropertyId = iri "urn:phase3:assertion:float"
        let booleanPropertyId = iri "urn:phase3:assertion:boolean"
        let dateTimePropertyId = iri "urn:phase3:assertion:date-time"
        let iriPropertyId = iri "urn:phase3:assertion:iri"
        let refPropertyId = iri "urn:phase3:assertion:ref"
        let listPropertyId = iri "urn:phase3:assertion:list"

        let propertyAnnotationId = iri "urn:phase3:annotation:property"
        let objectLiteralAnnotationId = iri "urn:phase3:annotation:object-literal"
        let objectTermAnnotationId = iri "urn:phase3:annotation:object-term"
        let objectLiteralUnitAnnotationId = iri "urn:phase3:annotation:object-literal-unit"
        let objectTermUnitAnnotationId = iri "urn:phase3:annotation:object-term-unit"

        let relationId = iri "urn:phase3:relation"
        let relationPropertyId = iri "urn:phase3:assertion:relation-property"
        let relationPropertyAnnotationId = iri "urn:phase3:annotation:relation-property"
        let relationAnnotationId = iri "urn:phase3:annotation:relation"

        let annotation id value evidence source =
            ArcAnnotation.create id annotationProperty value evidence source

        let propertyAnnotation =
            annotation
                propertyAnnotationId
                (AnnotationValue.Literal(ArcValue.String "property annotation"))
                (Some agentId)
                (Some resourceId)

        let stringProperty =
            ArcProperty.create
                stringPropertyId
                predicate
                (ArcValue.String "line 1\n\"Unicode 雪\" \\ \u0001")
                [ propertyAnnotation ]

        let exactTime =
            DateTimeOffset(2026, 8, 27, 12, 34, 56, TimeSpan.FromMinutes(330.0)).AddTicks(1234567L)

        let properties =
            [ stringProperty
              ArcProperty.create integerPropertyId predicate (ArcValue.Integer Int64.MinValue) Seq.empty
              ArcProperty.create floatPropertyId predicate (ArcValue.Float 1.23456789012345e-200) Seq.empty
              ArcProperty.create booleanPropertyId predicate (ArcValue.Boolean true) Seq.empty
              ArcProperty.create dateTimePropertyId predicate (ArcValue.DateTime exactTime) Seq.empty
              ArcProperty.create iriPropertyId predicate (ArcValue.Iri valueTerm) Seq.empty
              ArcProperty.create refPropertyId predicate (ArcValue.Ref agentId) Seq.empty
              ArcProperty.create
                  listPropertyId
                  predicate
                  (ArcValue.List
                      [ ArcValue.String "atomic"
                        ArcValue.Integer Int64.MaxValue
                        ArcValue.Float(BitConverter.Int64BitsToDouble Int64.MinValue)
                        ArcValue.List [ ArcValue.Boolean false ] ])
                  Seq.empty ]

        let objectAnnotations =
            [ annotation
                  objectLiteralAnnotationId
                  (AnnotationValue.Literal(ArcValue.Integer 42L))
                  None
                  None
              annotation objectTermAnnotationId (AnnotationValue.Term valueTerm) None None
              annotation
                  objectLiteralUnitAnnotationId
                  (AnnotationValue.LiteralWithUnit(ArcValue.Float 2.5, unitTerm))
                  None
                  None
              annotation
                  objectTermUnitAnnotationId
                  (AnnotationValue.TermWithUnit(valueTerm, unitTerm))
                  None
                  None ]

        let mainObject =
            ArcObject.create
                mainObjectId
                ArcObjectKind.Observable
                [ ArcTypeAssertion.create typeAssertionId typeTerm ]
                properties
                objectAnnotations

        let emptyObject id kind = ArcObject.create id kind Seq.empty Seq.empty Seq.empty

        let relationPropertyAnnotation =
            annotation
                relationPropertyAnnotationId
                (AnnotationValue.Term valueTerm)
                (Some agentId)
                None

        let relationProperty =
            ArcProperty.create
                relationPropertyId
                predicate
                (ArcValue.List [ ArcValue.String "left"; ArcValue.String "right" ])
                [ relationPropertyAnnotation ]

        let relationAnnotation =
            annotation
                relationAnnotationId
                (AnnotationValue.LiteralWithUnit(ArcValue.Integer 7L, unitTerm))
                None
                (Some resourceId)

        let relation =
            ArcRelation.create
                relationId
                mainObjectId
                relationPredicate
                agentId
                [ relationProperty ]
                [ relationAnnotation ]

        let terms =
            [ typeTerm, OntologyTerm.create (Some "Type") (Some "phase 3")
              predicate, OntologyTerm.create (Some "Predicate") (Some "phase 3")
              annotationProperty, OntologyTerm.create (Some "Annotation") (Some "phase 3")
              unitTerm, OntologyTerm.create (Some "Unit") None
              valueTerm, OntologyTerm.create None (Some "phase 3")
              relationPredicate, OntologyTerm.create (Some "Relation") (Some "phase 3")
              trickyTerm, OntologyTerm.create (Some "Escaped / ~ 雪") None ]
            |> Map.ofList

        let objects =
            [ mainObjectId, mainObject
              instrumentId, emptyObject instrumentId ArcObjectKind.Instrument
              resourceId, emptyObject resourceId ArcObjectKind.Resource
              activityId, emptyObject activityId ArcObjectKind.Activity
              agentId, emptyObject agentId ArcObjectKind.Agent
              roleId, emptyObject roleId ArcObjectKind.Role
              recipeId, emptyObject recipeId ArcObjectKind.Recipe
              collectionId, emptyObject collectionId ArcObjectKind.Collection
              selectorId, emptyObject selectorId ArcObjectKind.Selector ]
            |> Map.ofList

        { Terms = terms
          Objects = objects
          Relations = Map.ofList [ relationId, relation ] },
        { Term = typeTerm
          Object = mainObjectId
          TypeAssertion = typeAssertionId
          Property = stringPropertyId
          PropertyAnnotation = propertyAnnotationId
          ObjectAnnotation = objectTermAnnotationId
          Relation = relationId
          RelationProperty = relationPropertyId
          RelationPropertyAnnotation = relationPropertyAnnotationId
          RelationAnnotation = relationAnnotationId }
