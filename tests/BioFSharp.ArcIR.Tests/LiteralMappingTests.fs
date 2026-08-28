namespace BioFSharp.ArcIR.Tests

open System.IO
open Xunit
open BioFSharp.ArcIR

type LiteralMappingTests() =

    let iri = Iri.Create
    let objectId = iri "urn:literal-mapping:object"
    let endpointId = iri "urn:literal-mapping:endpoint"
    let relationId = iri "urn:literal-mapping:relation"
    let predicate = iri "urn:literal-mapping:term:predicate"
    let annotationProperty = iri "urn:literal-mapping:term:annotation"
    let relationPredicate = iri "urn:literal-mapping:term:relation"
    let target = iri "urn:literal-mapping:term:target"
    let unit = iri "urn:literal-mapping:term:unit"
    let objectPropertyId = iri "urn:literal-mapping:property:object"
    let objectPropertyAnnotationId = iri "urn:literal-mapping:annotation:object-property"
    let objectAnnotationId = iri "urn:literal-mapping:annotation:object"
    let relationPropertyId = iri "urn:literal-mapping:property:relation"
    let relationPropertyAnnotationId = iri "urn:literal-mapping:annotation:relation-property"
    let relationAnnotationId = iri "urn:literal-mapping:annotation:relation"

    let annotation id value = ArcAnnotation.create id annotationProperty value None None

    let graph =
        let objectPropertyAnnotation =
            annotation
                objectPropertyAnnotationId
                (AnnotationValue.Literal(ArcValue.String "object property annotation"))

        let objectProperty =
            ArcProperty.create
                objectPropertyId
                predicate
                (ArcValue.String "object property")
                [ objectPropertyAnnotation ]

        let objectAnnotation =
            annotation
                objectAnnotationId
                (AnnotationValue.LiteralWithUnit(ArcValue.String "object annotation", unit))

        let object' =
            ArcObject.create objectId ArcObjectKind.Observable Seq.empty [ objectProperty ] [ objectAnnotation ]

        let endpoint = ArcObject.create endpointId ArcObjectKind.Observable Seq.empty Seq.empty Seq.empty

        let relationPropertyAnnotation =
            annotation
                relationPropertyAnnotationId
                (AnnotationValue.Literal(ArcValue.String "relation property annotation"))

        let relationProperty =
            ArcProperty.create
                relationPropertyId
                predicate
                (ArcValue.String "relation property")
                [ relationPropertyAnnotation ]

        let relationAnnotation =
            annotation relationAnnotationId (AnnotationValue.Literal(ArcValue.String "relation annotation"))

        let relation =
            ArcRelation.create
                relationId
                objectId
                relationPredicate
                endpointId
                [ relationProperty ]
                [ relationAnnotation ]

        { Terms =
            [ predicate, OntologyTerm.create (Some "Predicate") None
              annotationProperty, OntologyTerm.create (Some "Annotation") None
              relationPredicate, OntologyTerm.create (Some "Relation") None
              target, OntologyTerm.create (Some "Target") None
              unit, OntologyTerm.create (Some "Unit") None ]
            |> Map.ofList
          Objects = Map.ofList [ objectId, object'; endpointId, endpoint ]
          Relations = Map.ofList [ relationId, relation ] }

    let apply command source =
        match LiteralMapping.apply command source with
        | Ok result -> result
        | Error failures -> failwithf "Expected literal mapping success, got %A" failures

    let resolved location source =
        use stream = new MemoryStream(Phase3Fixtures.bytes source, false)
        ArcIRJson.resolveLocation location stream |> Phase3Fixtures.expectOk

    [<Fact>]
    member _.``all supported property and annotation occurrences receive additive term companions``() =
        let commands =
            [ { Source = ArcJsonLocation.PropertyValue(objectId, objectPropertyId)
                Literal = "object property"
                Target = target }
              { Source =
                    ArcJsonLocation.PropertyAnnotationValue(
                        objectId,
                        objectPropertyId,
                        objectPropertyAnnotationId
                    )
                Literal = "object property annotation"
                Target = target }
              { Source = ArcJsonLocation.ObjectAnnotationValue(objectId, objectAnnotationId)
                Literal = "object annotation"
                Target = target }
              { Source = ArcJsonLocation.RelationPropertyValue(relationId, relationPropertyId)
                Literal = "relation property"
                Target = target }
              { Source =
                    ArcJsonLocation.RelationPropertyAnnotationValue(
                        relationId,
                        relationPropertyId,
                        relationPropertyAnnotationId
                    )
                Literal = "relation property annotation"
                Target = target }
              { Source = ArcJsonLocation.RelationAnnotationValue(relationId, relationAnnotationId)
                Literal = "relation annotation"
                Target = target } ]

        let mutable current = graph
        let applications = ResizeArray<LiteralTermMappingApplication>()

        for command in commands do
            let result = apply command current
            Assert.Equal(command.Source, result.Application.Input)
            Assert.Equal(command.Literal, result.Application.Literal)
            Assert.Equal(target, result.Application.Target)
            Assert.Equal(LiteralTermMappingStatus.Added, result.Application.Status)
            Assert.Equal(target.Value, (resolved result.Application.Output result.Graph).GetProperty("value").GetString())
            resolved command.Source result.Graph |> ignore
            Assert.Empty(Validation.validate result.Graph)
            applications.Add result.Application
            current <- result.Graph

        let objectSource = current.Objects.[objectId].Properties.[objectPropertyId]
        Assert.Equal(ArcValue.String "object property", objectSource.Value)

        let unitSource = resolved commands.[2].Source current
        Assert.Equal("literalWithUnit", unitSource.GetProperty("type").GetString())
        let unitCompanion = resolved applications.[2].Output current
        Assert.Equal("termWithUnit", unitCompanion.GetProperty("type").GetString())
        Assert.Equal(unit.Value, unitCompanion.GetProperty("unit").GetString())

        let decoded = Phase3Fixtures.bytes current |> Phase3Fixtures.readBytes
        Assert.Equal(current, decoded)

    [<Fact>]
    member _.``reapplying a compatible operation is deterministic and does not duplicate its companion``() =
        let command =
            { Source = ArcJsonLocation.PropertyValue(objectId, objectPropertyId)
              Literal = "object property"
              Target = target }

        let first = apply command graph
        let second = apply command first.Graph

        Assert.Equal(first.Graph, second.Graph)
        Assert.Equal(first.Application.Output, second.Application.Output)
        Assert.Equal(LiteralTermMappingStatus.AlreadyPresent, second.Application.Status)
        Assert.Equal(2, second.Graph.Objects.[objectId].Properties.Count)

    [<Fact>]
    member _.``missing wrong-kind mismatched and unregistered selections return typed failures``() =
        let expect expected command source =
            match LiteralMapping.apply command source with
            | Ok result -> failwithf "Expected failure, got %A" result
            | Error failures -> Assert.Contains(expected, failures)

        let location = ArcJsonLocation.PropertyValue(objectId, objectPropertyId)
        let command =
            { Source = location
              Literal = "object property"
              Target = target }

        expect
            (LiteralLocationNotFound(ArcJsonLocation.PropertyValue(objectId, iri "urn:literal-mapping:missing")))
            { command with Source = ArcJsonLocation.PropertyValue(objectId, iri "urn:literal-mapping:missing") }
            graph

        expect (SourceLiteralMismatch(location, "different", "object property")) { command with Literal = "different" } graph

        let wrongKindGraph =
            let owner = graph.Objects.[objectId]
            let property = owner.Properties.[objectPropertyId]
            let wrong = { property with Value = ArcValue.List [ ArcValue.String "object property" ] }
            { graph with
                Objects =
                    graph.Objects
                    |> Map.add objectId { owner with Properties = Map.add objectPropertyId wrong owner.Properties } }

        expect (ExpectedStringLiteral location) command wrongKindGraph

        let annotationLocation = ArcJsonLocation.ObjectAnnotationValue(objectId, objectAnnotationId)
        let annotationCommand =
            { Source = annotationLocation
              Literal = "object annotation"
              Target = target }
        let annotationOwner = graph.Objects.[objectId]
        let wrongAnnotation =
            { annotationOwner.Annotations.[objectAnnotationId] with
                Value = AnnotationValue.Term target }
        let wrongAnnotationGraph =
            { graph with
                Objects =
                    graph.Objects
                    |> Map.add
                        objectId
                        { annotationOwner with
                            Annotations =
                                Map.add objectAnnotationId wrongAnnotation annotationOwner.Annotations } }

        expect (ExpectedStringLiteral annotationLocation) annotationCommand wrongAnnotationGraph

        let absentTarget = iri "urn:literal-mapping:term:absent"
        expect (LiteralTargetTermNotRegistered absentTarget) { command with Target = absentTarget } graph

        expect
            (UnsupportedLiteralLocation(ArcJsonLocation.Property(objectId, objectPropertyId)))
            { command with Source = ArcJsonLocation.Property(objectId, objectPropertyId) }
            graph

    [<Fact>]
    member _.``deterministic companion conflicts and invalid graphs return no partial result``() =
        let command =
            { Source = ArcJsonLocation.PropertyValue(objectId, objectPropertyId)
              Literal = "object property"
              Target = target }

        let first = apply command graph
        let companionId =
            match first.Application.Output with
            | ArcJsonLocation.PropertyValue(_, assertionId) -> assertionId
            | output -> failwithf "Unexpected output %A" output

        let owner = first.Graph.Objects.[objectId]
        let companion = owner.Properties.[companionId]
        let conflicting = { companion with Value = ArcValue.String "collision" }
        let collisionGraph =
            { first.Graph with
                Objects =
                    first.Graph.Objects
                    |> Map.add objectId { owner with Properties = Map.add companionId conflicting owner.Properties } }

        match LiteralMapping.apply command collisionGraph with
        | Ok result -> failwithf "Expected collision, got %A" result
        | Error failures -> Assert.Contains(LiteralCompanionConflict(command.Source, companionId), failures)

        Assert.Equal(ArcValue.String "collision", collisionGraph.Objects.[objectId].Properties.[companionId].Value)

        let invalid = { graph with Terms = Map.remove predicate graph.Terms }

        match LiteralMapping.apply command invalid with
        | Ok result -> failwithf "Expected validation failure, got %A" result
        | Error [ InvalidLiteralMappingGraph issues ] -> Assert.NotEmpty issues
        | Error failures -> failwithf "Expected graph validation failure, got %A" failures
