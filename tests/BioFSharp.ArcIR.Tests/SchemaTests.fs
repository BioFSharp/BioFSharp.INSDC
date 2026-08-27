namespace BioFSharp.ArcIR.Tests

open System.IO
open System.Text.Json
open Xunit
open BioFSharp.ArcIR

type SchemaTests() =

    [<Fact>]
    member _.``version one schema is committed with the canonical identity and closed roots``() =
        use document = JsonDocument.Parse(File.ReadAllBytes Phase3Fixtures.schemaPath)
        let root = document.RootElement

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString())
        Assert.Equal(ArcIRJson.SchemaId, root.GetProperty("$id").GetString())
        Assert.Equal(ArcIRJson.FormatVersion, root.GetProperty("properties").GetProperty("formatVersion").GetProperty("const").GetString())
        Assert.False(root.GetProperty("additionalProperties").GetBoolean())

        let graph = root.GetProperty("$defs").GetProperty("graph")
        Assert.False(graph.GetProperty("additionalProperties").GetBoolean())

    [<Fact>]
    member _.``all committed ArcIR fixtures decode and recanonicalize byte for byte``() =
        for path in Directory.GetFiles(Path.GetDirectoryName(Phase3Fixtures.fixturePath "placeholder"), "*.arcir.json") do
            let bytes = File.ReadAllBytes path
            let decoded = Phase3Fixtures.readBytes bytes
            let recanonical = Phase3Fixtures.bytes decoded

            Assert.True((bytes = recanonical), sprintf "Fixture is not canonical: %s" path)
