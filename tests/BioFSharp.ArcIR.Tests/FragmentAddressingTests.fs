namespace BioFSharp.ArcIR.Tests

open System.IO
open System.Text.Json
open Xunit
open BioFSharp.ArcIR

type FragmentAddressingTests() =

    [<Fact>]
    member _.``selector applies JSON Pointer escaping before URI fragment encoding``() =
        let id = Phase3Fixtures.iri "https://example.org/a/~snow/雪#part%2F"
        let selector = ArcIRJson.selector (ArcJsonLocation.Term id)

        Assert.Equal(ArcIRJson.JsonPointerConformsTo, selector.ConformsTo)
        Assert.Equal(
            "#/graph/terms/https:~1~1example.org~1a~1~0snow~1%E9%9B%AA%23part%252F",
            selector.Value
        )
        Assert.Equal(ArcJsonLocation.Term id, ArcIRJson.parseLocation selector |> Phase3Fixtures.expectOk)

    [<Fact>]
    member _.``all sixteen typed location cases enumerate and resolve``() =
        let graph = Phase3Fixtures.comprehensiveGraph
        let ids: Phase3Fixtures.LocationIds = Phase3Fixtures.locationIds
        let expected =
            [ ArcJsonLocation.Term ids.Term
              ArcJsonLocation.Object ids.Object
              ArcJsonLocation.TypeAssertion(ids.Object, ids.TypeAssertion)
              ArcJsonLocation.Property(ids.Object, ids.Property)
              ArcJsonLocation.PropertyValue(ids.Object, ids.Property)
              ArcJsonLocation.ObjectAnnotation(ids.Object, ids.ObjectAnnotation)
              ArcJsonLocation.ObjectAnnotationValue(ids.Object, ids.ObjectAnnotation)
              ArcJsonLocation.PropertyAnnotation(ids.Object, ids.Property, ids.PropertyAnnotation)
              ArcJsonLocation.PropertyAnnotationValue(ids.Object, ids.Property, ids.PropertyAnnotation)
              ArcJsonLocation.Relation ids.Relation
              ArcJsonLocation.RelationProperty(ids.Relation, ids.RelationProperty)
              ArcJsonLocation.RelationPropertyValue(ids.Relation, ids.RelationProperty)
              ArcJsonLocation.RelationPropertyAnnotation(
                  ids.Relation,
                  ids.RelationProperty,
                  ids.RelationPropertyAnnotation
              )
              ArcJsonLocation.RelationPropertyAnnotationValue(
                  ids.Relation,
                  ids.RelationProperty,
                  ids.RelationPropertyAnnotation
              )
              ArcJsonLocation.RelationAnnotation(ids.Relation, ids.RelationAnnotation)
              ArcJsonLocation.RelationAnnotationValue(ids.Relation, ids.RelationAnnotation) ]

        let enumerated = ArcIRJson.locations graph |> Set.ofSeq

        for location in expected do
            Assert.Contains(location, enumerated)
            Assert.Equal(location, ArcIRJson.selector location |> ArcIRJson.parseLocation |> Phase3Fixtures.expectOk)
            use stream = new MemoryStream(Phase3Fixtures.bytes graph, false)

            match ArcIRJson.resolveLocation location stream with
            | Ok fragment -> Assert.NotEqual(JsonValueKind.Undefined, fragment.ValueKind)
            | Error errors -> failwithf "Location %A did not resolve: %A" location errors

            Assert.True(stream.CanRead)

    [<Fact>]
    member _.``list and term annotation value locations resolve as atomic values``() =
        let graph = Phase3Fixtures.comprehensiveGraph
        let ids: Phase3Fixtures.LocationIds = Phase3Fixtures.locationIds
        let bytes = Phase3Fixtures.bytes graph
        let listProperty = Phase3Fixtures.iri "urn:phase3:assertion:list"

        use listStream = new MemoryStream(bytes, false)
        let listValue =
            ArcIRJson.resolveLocation (ArcJsonLocation.PropertyValue(ids.Object, listProperty)) listStream
            |> Phase3Fixtures.expectOk

        Assert.Equal("list", listValue.GetProperty("type").GetString())
        Assert.Equal(4, listValue.GetProperty("value").GetArrayLength())

        use termStream = new MemoryStream(bytes, false)
        let termValue =
            ArcIRJson.resolveLocation
                (ArcJsonLocation.ObjectAnnotationValue(ids.Object, ids.ObjectAnnotation))
                termStream
            |> Phase3Fixtures.expectOk

        Assert.Equal("term", termValue.GetProperty("type").GetString())
        Assert.Equal("urn:phase3:term:value", termValue.GetProperty("value").GetString())

    [<Fact>]
    member _.``resolver rejects missing malformed and wrong-conformance selectors``() =
        let graph = Phase3Fixtures.simpleState "value"
        let bytes = Phase3Fixtures.bytes graph
        let missing =
            ArcIRJson.selector
                (ArcJsonLocation.Object(Phase3Fixtures.iri "urn:phase3:does-not-exist"))

        use missingStream = new MemoryStream(bytes, false)
        ArcIRJson.resolve missing missingStream
        |> Phase3Fixtures.expectErrorCode "arcir.json.fragment-not-found"

        let wrong =
            { missing with
                ConformsTo = Phase3Fixtures.iri "https://example.org/selectors/other" }

        use wrongStream = new MemoryStream(bytes, false)
        ArcIRJson.resolve wrong wrongStream
        |> Phase3Fixtures.expectErrorCode "arcir.json.unsupported-selector"

        let malformed =
            { ConformsTo = ArcIRJson.JsonPointerConformsTo
              Value = "#/graph/%GG" }

        use malformedStream = new MemoryStream(bytes, false)
        ArcIRJson.resolve malformed malformedStream
        |> Phase3Fixtures.expectErrorCode "arcir.json.invalid-selector"

        let rawSpace =
            { ConformsTo = ArcIRJson.JsonPointerConformsTo
              Value = "#/graph/a b" }

        use rawSpaceStream = new MemoryStream(bytes, false)
        ArcIRJson.resolve rawSpace rawSpaceStream
        |> Phase3Fixtures.expectErrorCode "arcir.json.invalid-selector"

        ArcIRJson.parseLocation wrong
        |> Phase3Fixtures.expectErrorCode "arcir.json.unsupported-selector"

        ArcIRJson.parseLocation malformed
        |> Phase3Fixtures.expectErrorCode "arcir.json.invalid-selector"

    [<Fact>]
    member _.``typed location parser rejects unsupported shapes and invalid identity tokens``() =
        let selector value =
            { ConformsTo = ArcIRJson.JsonPointerConformsTo
              Value = value }

        selector "#/graph/objects/urn:example:object/properties/urn:example:assertion/predicate"
        |> ArcIRJson.parseLocation
        |> Phase3Fixtures.expectErrorCode "arcir.json.unsupported-location"

        selector "#/graph/objects/relative"
        |> ArcIRJson.parseLocation
        |> Phase3Fixtures.expectErrorCode "arcir.json.invalid-location"

    [<Fact>]
    member _.``resolver refuses ambiguous duplicate object members``() =
        let duplicate = "{\"graph\":{\"objects\":{},\"objects\":{}}}"
        let selector =
            { ConformsTo = ArcIRJson.JsonPointerConformsTo
              Value = "#/graph/objects" }

        use stream = new MemoryStream(Phase3Fixtures.utf8 duplicate, false)
        ArcIRJson.resolve selector stream
        |> Phase3Fixtures.expectErrorCode "arcir.json.duplicate-member"
