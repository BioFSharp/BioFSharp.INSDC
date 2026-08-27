namespace BioFSharp.ArcIR.Tests

open System.IO
open Xunit
open BioFSharp.ArcIR

type SemanticMappingTests() =

    let iri = Iri.Create
    let sourceTerm = iri "urn:example:source-term"
    let targetTerm = iri "urn:example:target-term"
    let ownerId = iri "urn:example:owner"
    let endpointId = iri "urn:example:endpoint"
    let typeId = iri "urn:example:type-assertion"
    let propertyId = iri "urn:example:property-assertion"
    let annotationId = iri "urn:example:annotation-assertion"
    let relationId = iri "urn:example:relation"

    let sourceDefinition = OntologyTerm.create (Some "Source term") (Some "source ontology")
    let targetDefinition = OntologyTerm.create (Some "Target term") (Some "target ontology")

    let claim =
        { Id = iri "urn:example:mapping-claim"
          Subject = sourceTerm
          Predicate = iri "http://www.w3.org/2004/02/skos/core#exactMatch"
          Object = targetTerm
          SubjectDefinition = Some sourceDefinition
          ObjectDefinition = targetDefinition
          Justification = Some(iri "https://w3id.org/semapv/vocab/ManualMappingCuration") }

    let sourceGraph =
        let property = ArcProperty.create propertyId sourceTerm (ArcValue.Iri sourceTerm) Seq.empty

        let annotation =
            ArcAnnotation.create annotationId sourceTerm (AnnotationValue.Term sourceTerm) None None

        let owner =
            ArcObject.create
                ownerId
                ArcObjectKind.Collection
                [ ArcTypeAssertion.create typeId sourceTerm ]
                [ property ]
                [ annotation ]

        let endpoint = ArcObject.create endpointId ArcObjectKind.Observable Seq.empty Seq.empty Seq.empty
        let relation = ArcRelation.create relationId ownerId sourceTerm endpointId Seq.empty Seq.empty

        { Terms = Map.ofList [ sourceTerm, sourceDefinition ]
          Objects = Map.ofList [ ownerId, owner; endpointId, endpoint ]
          Relations = Map.ofList [ relationId, relation ] }

    let apply claim graph =
        match SemanticMapping.applyClaim claim graph with
        | Ok value -> value
        | Error errors -> failwithf "Expected mapping success, got %A" errors

    [<Fact>]
    member _.``mapping adds companions for every semantic term role and preserves sources`` () =
        let result = apply claim sourceGraph
        let owner = result.Graph.Objects.[ownerId]

        Assert.Equal(6, result.Applications.Length)
        Assert.Equal(2, owner.Types.Count)
        Assert.Equal(3, owner.Properties.Count)
        Assert.Equal(3, owner.Annotations.Count)
        Assert.Equal(2, result.Graph.Relations.Count)

        Assert.Equal(sourceTerm, owner.Types.[typeId].Term)
        Assert.Equal(sourceTerm, owner.Properties.[propertyId].Predicate)
        Assert.Equal(ArcValue.Iri sourceTerm, owner.Properties.[propertyId].Value)
        Assert.Equal(sourceTerm, owner.Annotations.[annotationId].Property)
        Assert.Equal(AnnotationValue.Term sourceTerm, owner.Annotations.[annotationId].Value)
        Assert.Equal(sourceTerm, result.Graph.Relations.[relationId].Predicate)

        Assert.Contains(owner.Types.Values, fun assertion -> assertion.Term = targetTerm)
        Assert.Contains(owner.Properties.Values, fun assertion -> assertion.Predicate = targetTerm)
        Assert.Contains(owner.Properties.Values, fun assertion -> assertion.Value = ArcValue.Iri targetTerm)
        Assert.Contains(owner.Annotations.Values, fun assertion -> assertion.Property = targetTerm)
        Assert.Contains(owner.Annotations.Values, fun assertion -> assertion.Value = AnnotationValue.Term targetTerm)
        Assert.Contains(result.Graph.Relations.Values, fun relation -> relation.Predicate = targetTerm)
        Assert.Equal(targetDefinition, result.Graph.Terms.[targetTerm])
        Assert.Empty(Validation.validate result.Graph)

        let bytes = Phase3Fixtures.bytes result.Graph

        for application in result.Applications do
            use inputStream = new MemoryStream(bytes, false)
            use outputStream = new MemoryStream(bytes, false)
            ArcIRJson.resolveLocation application.Input inputStream |> Phase3Fixtures.expectOk |> ignore
            ArcIRJson.resolveLocation application.Output outputStream |> Phase3Fixtures.expectOk |> ignore

    [<Fact>]
    member _.``reapplying a claim is graph-idempotent and reports reused companions`` () =
        let first = apply claim sourceGraph
        let second = apply claim first.Graph

        Assert.Equal(first.Graph, second.Graph)
        Assert.Equal(first.Applications.Length, second.Applications.Length)

        Assert.All(
            second.Applications,
            fun application -> Assert.Equal(MappingApplicationStatus.AlreadyPresent, application.Status)
        )

    [<Fact>]
    member _.``an unused claim is a true no-op and does not register its target`` () =
        let absentClaim =
            { claim with
                Id = iri "urn:example:absent-claim"
                Subject = iri "urn:example:absent-source"
                Object = iri "urn:example:absent-target" }

        let result = apply absentClaim sourceGraph

        Assert.Equal(sourceGraph, result.Graph)
        Assert.Empty result.Applications
        Assert.False(result.Graph.Terms.ContainsKey absentClaim.Object)

    [<Fact>]
    member _.``a conflicting target definition returns no enriched graph`` () =
        let conflictingGraph =
            { sourceGraph with
                Terms =
                    sourceGraph.Terms
                    |> Map.add targetTerm (OntologyTerm.create (Some "Different definition") None) }

        match SemanticMapping.applyClaim claim conflictingGraph with
        | Ok value -> failwithf "Expected mapping conflict, got %A" value
        | Error [ MappingTermConflict(id, _, incoming) ] ->
            Assert.Equal(targetTerm, id)
            Assert.Equal(targetDefinition, incoming)
        | Error errors -> failwithf "Expected one target-term conflict, got %A" errors

        Assert.Equal(sourceTerm, conflictingGraph.Objects.[ownerId].Types.[typeId].Term)

    [<Fact>]
    member _.``a deterministic companion identity collision returns no graph`` () =
        let first = apply claim sourceGraph

        let mappedTypeId =
            first.Applications
            |> List.pick (fun application ->
                match application.Input, application.Output with
                | ArcJsonLocation.TypeAssertion(_, inputId), ArcJsonLocation.TypeAssertion(_, outputId)
                    when inputId = typeId -> Some outputId
                | _ -> None)

        let owner = first.Graph.Objects.[ownerId]

        let collision =
            { first.Graph with
                Objects =
                    first.Graph.Objects
                    |> Map.add
                        ownerId
                        { owner with
                            Types = owner.Types |> Map.add mappedTypeId (ArcTypeAssertion.create mappedTypeId sourceTerm) } }

        match SemanticMapping.applyClaim claim collision with
        | Ok value -> failwithf "Expected assertion collision, got %A" value
        | Error errors ->
            Assert.Contains(
                errors,
                fun error ->
                    match error with
                    | MappedAssertionConflict(conflictOwner, assertion) ->
                        conflictOwner = ownerId && assertion = mappedTypeId
                    | _ -> false
            )
