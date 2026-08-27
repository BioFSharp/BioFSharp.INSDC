namespace BioFSharp.INSDC.Tests

open Xunit
open BioFSharp.IO.INSDC
open BioFSharp.ArcIR
open BioFSharp.INSDC.ArcIR

type SssomMappingTests() =

    let mappingText = TestFiles.fixtureText "sssom/INSDCER-ARC.sssom.tsv"

    let loaded = SssomMapping.loadEmbedded mappingText
    let exactClaims = SssomMapping.exactMatches loaded

    let sourceGraph =
        let project = BioProject.read (TestFiles.fixture "PRJDB5192.xml") |> Seq.exactlyOne
        let study = Study.read (TestFiles.fixture "DRP003416.xml") |> Seq.exactlyOne
        [ INSDC.bioProject project; INSDC.study study ] |> INSDC.build

    let apply claims graph =
        match SemanticMapping.applyClaims claims graph with
        | Ok value -> value
        | Error errors -> failwithf "Expected mapping success, got %A" errors

    [<Fact>]
    member _.``PolyglotSSSOM projects all reviewed base rows into absolute neutral claims`` () =
        let errors =
            loaded.Diagnostics
            |> List.filter (fun diagnostic -> diagnostic.Severity = DiagnosticSeverity.Error)

        Assert.Empty errors
        Assert.Equal(7, loaded.Claims.Length)
        Assert.Equal(7, exactClaims.Length)

        for claim in loaded.Claims do
            Assert.True(claim.Id.Value.StartsWith "https://")
            Assert.True(claim.Subject.Value.StartsWith "https://")
            Assert.Equal(SssomMapping.exactMatchPredicate, claim.Predicate)
            Assert.True(claim.Object.Value.StartsWith "https://")
            Assert.True(claim.Justification.IsSome)
            Assert.True(claim.SubjectDefinition.IsSome)
            Assert.True(claim.ObjectDefinition.Name.IsSome)

    [<Fact>]
    member _.``base mappings add same-value ARC annotations beside their INSDCER sources`` () =
        let result = apply exactClaims sourceGraph
        let mutable expectedApplications = 0

        for claim in exactClaims do
            for sourceObject in sourceGraph.Objects.Values do
                let sources =
                    sourceObject.Annotations.Values
                    |> Seq.filter (fun annotation -> annotation.Property = claim.Subject)
                    |> List.ofSeq

                if not (List.isEmpty sources) then
                    expectedApplications <- expectedApplications + sources.Length
                    let mappedObject = result.Graph.Objects.[sourceObject.Id]

                    for source in sources do
                        Assert.Equal(source, mappedObject.Annotations.[source.Id])

                        let companions =
                            mappedObject.Annotations.Values
                            |> Seq.filter (fun annotation ->
                                annotation.Property = claim.Object
                                && annotation.Value = source.Value)
                            |> List.ofSeq

                        Assert.Single companions |> ignore
                        Assert.NotEqual(source.Id, companions.Head.Id)

        Assert.Equal(5, expectedApplications)
        Assert.Equal(expectedApplications, result.Applications.Length)
        Assert.All(result.Applications, fun application -> Assert.Equal(MappingApplicationStatus.Added, application.Status))

        let nonEndpointIssues =
            Validation.validate result.Graph
            |> List.filter (function
                | MissingEndpoint _ -> false
                | _ -> true)

        Assert.Empty nonEndpointIssues

        let repeated = apply exactClaims result.Graph
        Assert.Equal(result.Graph, repeated.Graph)
        Assert.Equal(expectedApplications, repeated.Applications.Length)

        Assert.All(
            repeated.Applications,
            fun application -> Assert.Equal(MappingApplicationStatus.AlreadyPresent, application.Status)
        )

    [<Fact>]
    member _.``base rows absent from this fixture do not add unused endpoint terms`` () =
        let result = apply exactClaims sourceGraph

        let unusedClaims =
            exactClaims
            |> List.filter (fun claim ->
                sourceGraph.Objects.Values
                |> Seq.collect (fun object' -> object'.Annotations.Values)
                |> Seq.exists (fun annotation -> annotation.Property = claim.Subject)
                |> not)

        Assert.Equal(2, unusedClaims.Length)

        for claim in unusedClaims do
            Assert.False(result.Graph.Terms.ContainsKey claim.Object)

    [<Fact>]
    member _.``a row without stable record identity is diagnosed and not projected`` () =
        let invalid =
            mappingText.Replace(
                "insdcarc:INSDCER_1000001-INVMSO_00000008\tINSDCER:1000001",
                "\tINSDCER:1000001"
            )

        let result = SssomMapping.loadEmbedded invalid

        Assert.Empty result.Claims

        Assert.Contains(
            result.Diagnostics,
            fun diagnostic -> diagnostic.Severity = DiagnosticSeverity.Error
        )
