namespace BioFSharp.ArcIR.Tests

open System
open Xunit
open BioFSharp.ArcIR

module private Fixtures =

    let iri value = Iri.Create value
    let termId = iri "https://example.org/type"
    let predicate = iri "https://example.org/property"

    let term = OntologyTerm.create (Some "Type") (Some "example")

    let object' id kind types properties =
        ArcObject.create id kind types properties Seq.empty

    let typeAssertion id term = ArcTypeAssertion.create id term

    let property id predicate value = ArcProperty.create id predicate value Seq.empty

    let ok result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "Expected success, got %A" error

type IriTests() =

    [<Fact>]
    member _.``Iri accepts absolute identifiers and preserves their text``() =
        let value = "urn:example:thing"
        Assert.Equal(value, (Iri.Create value).Value)

    [<Theory>]
    [<InlineData("")>]
    [<InlineData("relative/path")>]
    [<InlineData("bare-accession")>]
    member _.``Iri rejects empty and relative identifiers``(value: string) =
        Assert.Throws<ArgumentException>(fun () -> Iri.Create value |> ignore) |> ignore

type GraphOperationTests() =

    [<Fact>]
    member _.``strict add rejects an incompatible duplicate object``() =
        let id = Fixtures.iri "urn:example:object"
        let first = Fixtures.object' id ArcObjectKind.Collection Seq.empty Seq.empty
        let second = Fixtures.object' id ArcObjectKind.Activity Seq.empty Seq.empty
        let graph = ArcIR.addObject first ArcIR.Empty |> Fixtures.ok

        match ArcIR.addObject second graph with
        | Error(ObjectConflict(conflictId, _, _)) -> Assert.Equal(id, conflictId)
        | result -> failwithf "Expected an object conflict, got %A" result

    [<Fact>]
    member _.``upsert returns the replaced object``() =
        let id = Fixtures.iri "urn:example:object"
        let first = Fixtures.object' id ArcObjectKind.Collection Seq.empty Seq.empty
        let second = Fixtures.object' id ArcObjectKind.Activity Seq.empty Seq.empty
        let graph = ArcIR.addObject first ArcIR.Empty |> Fixtures.ok
        let updated, replaced = ArcIR.upsertObject second graph

        Assert.Equal(Some first, replaced)
        Assert.Equal(second, updated.Objects.[id])

    [<Fact>]
    member _.``merge combines disjoint assertions on one stable object``() =
        let objectId = Fixtures.iri "urn:example:object"
        let typeId = Fixtures.iri "urn:example:type-assertion"
        let propertyId = Fixtures.iri "urn:example:property-assertion"
        let leftObject =
            Fixtures.object'
                objectId
                ArcObjectKind.Collection
                [ Fixtures.typeAssertion typeId Fixtures.termId ]
                Seq.empty
        let rightObject =
            Fixtures.object'
                objectId
                ArcObjectKind.Collection
                Seq.empty
                [ Fixtures.property propertyId Fixtures.predicate (ArcValue.String "value") ]
        let terms =
            Map.ofList
                [ Fixtures.termId, Fixtures.term
                  Fixtures.predicate, OntologyTerm.create (Some "Property") (Some "example") ]
        let left = { ArcIR.Empty with Terms = terms; Objects = Map.ofList [ objectId, leftObject ] }
        let right = { ArcIR.Empty with Terms = terms; Objects = Map.ofList [ objectId, rightObject ] }
        let merged = ArcIR.merge left right |> Fixtures.ok

        Assert.Single(merged.Objects.[objectId].Types) |> ignore
        Assert.Single(merged.Objects.[objectId].Properties) |> ignore

    [<Fact>]
    member _.``merge reports an incompatible assertion without selecting a winner``() =
        let objectId = Fixtures.iri "urn:example:object"
        let propertyId = Fixtures.iri "urn:example:property-assertion"
        let graphWith value =
            let object' =
                Fixtures.object'
                    objectId
                    ArcObjectKind.Collection
                    Seq.empty
                    [ Fixtures.property propertyId Fixtures.predicate (ArcValue.String value) ]
            { ArcIR.Empty with Objects = Map.ofList [ objectId, object' ] }

        match ArcIR.merge (graphWith "left") (graphWith "right") with
        | Error conflicts -> Assert.Contains(AssertionConflict(objectId, propertyId), conflicts)
        | Ok _ -> failwith "Expected an assertion conflict."

type ValidationTests() =

    [<Fact>]
    member _.``validation reports missing terms endpoints and duplicate semantic types``() =
        let objectId = Fixtures.iri "urn:example:object"
        let missingId = Fixtures.iri "urn:example:missing"
        let typeOne = Fixtures.iri "urn:example:type-one"
        let typeTwo = Fixtures.iri "urn:example:type-two"
        let relationId = Fixtures.iri "urn:example:relation"
        let object' =
            Fixtures.object'
                objectId
                ArcObjectKind.Collection
                [ Fixtures.typeAssertion typeOne Fixtures.termId
                  Fixtures.typeAssertion typeTwo Fixtures.termId ]
                Seq.empty
        let relation = ArcRelation.create relationId objectId Fixtures.predicate missingId Seq.empty Seq.empty
        let graph =
            { ArcIR.Empty with
                Objects = Map.ofList [ objectId, object' ]
                Relations = Map.ofList [ relationId, relation ] }
        let issues = Validation.validate graph

        Assert.Contains(MissingTerm(objectId, Fixtures.termId), issues)
        Assert.Contains(MissingTerm(relationId, Fixtures.predicate), issues)
        Assert.Contains(MissingEndpoint(relationId, missingId), issues)
        Assert.Contains(DuplicateTypeAssertion(objectId, Fixtures.termId, [ typeOne; typeTwo ]), issues)
