namespace BioFSharp.ArcIR.Tests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open BioFSharp.ArcIR

type StateArtifactTests() =

    let withTemporaryDirectory action =
        let directory =
            Path.Combine(Path.GetTempPath(), "BioFSharp.ArcIR.Tests-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory directory |> ignore

        try
            action directory
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``artifact revisions validate metadata and expose normalized values``() =
        let uppercaseDigest = String.replicate 64 "A"
        let revision = ArtifactRevision.create "arcir/state.json" uppercaseDigest (Some "commit-id")

        Assert.Equal("arcir/state.json", revision.Path)
        Assert.Equal(String.replicate 64 "a", revision.Sha256)
        Assert.Equal(Some "commit-id", revision.Commit)
        Assert.Throws<ArgumentException>(fun () -> ArtifactRevision.create "" uppercaseDigest None |> ignore)
        |> ignore
        Assert.Throws<ArgumentException>(fun () -> ArtifactRevision.create "state.json" "not-a-digest" None |> ignore)
        |> ignore
        Assert.Throws<ArgumentException>(fun () -> ArtifactRevision.create "state.json" uppercaseDigest (Some " ") |> ignore)
        |> ignore

    [<Fact>]
    member _.``writeNew publishes canonical bytes and their exact revision``() =
        withTemporaryDirectory (fun directory ->
            let path = Path.Combine(directory, "nested", "state.arcir.json")
            let graph = Phase3Fixtures.simpleState "published"
            let expected = Phase3Fixtures.bytes graph
            let revision = ArcIRJson.writeNew path graph |> Phase3Fixtures.expectOk
            let actual = File.ReadAllBytes path

            Assert.Equal(path, revision.Path)
            Assert.Equal(None, revision.Commit)
            Assert.True((expected = actual))
            Assert.True(ArtifactRevision.verifyBytes revision actual)
            Assert.True((graph = (ArcIRJson.readRevision revision |> Phase3Fixtures.expectOk))))

    [<Fact>]
    member _.``writeNew refuses replacement without changing bytes or leaving temporary files``() =
        withTemporaryDirectory (fun directory ->
            let path = Path.Combine(directory, "state.arcir.json")
            let sentinel = [| 0uy; 1uy; 2uy; 255uy |]
            File.WriteAllBytes(path, sentinel)

            ArcIRJson.writeNew path (Phase3Fixtures.simpleState "replacement")
            |> Phase3Fixtures.expectErrorCode "arcir.json.state-exists"

            Assert.True((sentinel = File.ReadAllBytes path))
            Assert.Equal<string array>([| path |], Directory.GetFiles directory))

    [<Fact>]
    member _.``directory-only state paths fail without creating the directory``() =
        withTemporaryDirectory (fun directory ->
            let targetDirectory = Path.Combine(directory, "not-a-state")
            let directoryOnlyPath = targetDirectory + string Path.DirectorySeparatorChar

            ArcIRJson.writeNew directoryOnlyPath (Phase3Fixtures.simpleState "value")
            |> Phase3Fixtures.expectErrorCode "arcir.json.invalid-path"

            Assert.False(Directory.Exists targetDirectory))

    [<Fact>]
    member _.``serialization failure creates neither destination nor temporary artifact``() =
        withTemporaryDirectory (fun directory ->
            let path = Path.Combine(directory, "state.arcir.json")

            ArcIRJson.writeNew path (Phase3Fixtures.simpleState null)
            |> Phase3Fixtures.expectErrorCode "arcir.json.invalid-string"

            Assert.False(File.Exists path)
            Assert.Empty(Directory.GetFiles directory))

    [<Fact>]
    member _.``concurrent writes publish exactly one complete immutable state``() =
        withTemporaryDirectory (fun directory ->
            let path = Path.Combine(directory, "state.arcir.json")
            let graph = Phase3Fixtures.simpleState "race"
            let expected = Phase3Fixtures.bytes graph
            let writes =
                [| Task.Run(fun () -> ArcIRJson.writeNew path graph)
                   Task.Run(fun () -> ArcIRJson.writeNew path graph) |]

            Task.WaitAll(writes |> Array.map (fun task -> task :> Task))

            let results = writes |> Array.map (fun task -> task.Result)
            let successes = results |> Array.choose (function Ok value -> Some value | Error _ -> None)
            let failures = results |> Array.choose (function Error value -> Some value | Ok _ -> None)

            Assert.Single(successes) |> ignore
            Assert.Single(failures) |> ignore
            Assert.Contains(failures.[0], fun error -> error.Code = "arcir.json.state-exists")
            Assert.True((expected = File.ReadAllBytes path)))

    [<Fact>]
    member _.``two states keep one assertion IRI while artifact qualification disambiguates occurrences``() =
        let beforePath = Phase3Fixtures.fixturePath "state-before.arcir.json"
        let afterPath = Phase3Fixtures.fixturePath "state-after.arcir.json"
        let beforeBytes = File.ReadAllBytes beforePath
        let afterBytes = File.ReadAllBytes afterPath
        let beforeRevision = ArtifactRevision.ofBytes beforePath (Some "commit-before") beforeBytes
        let afterRevision = ArtifactRevision.ofBytes afterPath (Some "commit-after") afterBytes
        let beforeRef = ArcIRJson.fragmentRef beforeRevision Phase3Fixtures.simpleLocation
        let afterRef = ArcIRJson.fragmentRef afterRevision Phase3Fixtures.simpleLocation

        Assert.Equal(beforeRef.Selector, afterRef.Selector)
        Assert.NotEqual(beforeRef.Artifact, afterRef.Artifact)

        let lexicalValue fragment =
            ArcIRJson.resolveFragment fragment
            |> Phase3Fixtures.expectOk
            |> fun element -> element.GetProperty("value").GetString()

        Assert.Equal("before", lexicalValue beforeRef)
        Assert.Equal("after", lexicalValue afterRef)

    [<Fact>]
    member _.``tampered artifact fails digest checked reads and fragment resolution``() =
        withTemporaryDirectory (fun directory ->
            let path = Path.Combine(directory, "state.arcir.json")
            let revision =
                ArcIRJson.writeNew path (Phase3Fixtures.simpleState "original")
                |> Phase3Fixtures.expectOk

            File.WriteAllBytes(path, Phase3Fixtures.bytes (Phase3Fixtures.simpleState "tampered"))

            ArcIRJson.readRevision revision
            |> Phase3Fixtures.expectErrorCode "arcir.json.digest-mismatch"

            ArcIRJson.fragmentRef revision Phase3Fixtures.simpleLocation
            |> ArcIRJson.resolveFragment
            |> Phase3Fixtures.expectErrorCode "arcir.json.digest-mismatch")
