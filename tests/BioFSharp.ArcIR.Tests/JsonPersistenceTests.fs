namespace BioFSharp.ArcIR.Tests

open System
open System.IO
open System.Text
open Xunit
open BioFSharp.ArcIR

type JsonPersistenceTests() =

    [<Fact>]
    member _.``representative graph has semantic and byte-identical round trips``() =
        let graph = Phase3Fixtures.comprehensiveGraph
        let firstBytes = Phase3Fixtures.bytes graph
        let decoded = Phase3Fixtures.readBytes firstBytes
        let secondBytes = Phase3Fixtures.bytes decoded

        Assert.True((graph = decoded), sprintf "Decoded graph differs:\n%A" decoded)
        Assert.True((firstBytes = secondBytes), "Canonical bytes changed after decoding and encoding.")
        Assert.Equal(byte '{', firstBytes.[0])
        Assert.Equal(byte '\n', Array.last firstBytes)
        Assert.False(firstBytes.Length >= 3 && firstBytes.[0..2] = [| 0xEFuy; 0xBBuy; 0xBFuy |])

        let text = Encoding.UTF8.GetString firstBytes
        Assert.Contains("雪", text)
        Assert.Contains("\"value\": \"1.23456789012345E-200\"", text)
        Assert.Contains("\"value\": \"-0\"", text)

    [<Fact>]
    member _.``committed fixture is the canonical output for the same state``() =
        let expected = File.ReadAllBytes(Phase3Fixtures.fixturePath "state-before.arcir.json")
        let actual = Phase3Fixtures.simpleState "before" |> Phase3Fixtures.bytes

        Assert.Equal(Encoding.UTF8.GetString expected, Encoding.UTF8.GetString actual)
        Assert.True((expected = actual), "The committed golden state has drifted from canonical output bytes.")
        Assert.True((Phase3Fixtures.readBytes expected = Phase3Fixtures.simpleState "before"))

    [<Fact>]
    member _.``canonical maps use ordinal identifier ordering``() =
        let firstId = Phase3Fixtures.iri "urn:phase3:term:A"
        let secondId = Phase3Fixtures.iri "urn:phase3:term:a"
        let thirdId = Phase3Fixtures.iri "urn:phase3:term:雪"
        let term = OntologyTerm.create None None
        let graph =
            { ArcIR.Empty with
                Terms = Map.ofList [ thirdId, term; secondId, term; firstId, term ] }

        let text = ArcIRJson.writeString graph |> Phase3Fixtures.expectOk
        let firstIndex = text.IndexOf(firstId.Value, StringComparison.Ordinal)
        let secondIndex = text.IndexOf(secondId.Value, StringComparison.Ordinal)
        let thirdIndex = text.IndexOf(thirdId.Value, StringComparison.Ordinal)

        Assert.True(firstIndex >= 0)
        Assert.True(firstIndex < secondIndex)
        Assert.True(secondIndex < thirdIndex)

    [<Fact>]
    member _.``stream reader and writer leave caller owned streams open``() =
        use output = new MemoryStream()
        let graph = Phase3Fixtures.simpleState "stream"

        ArcIRJson.Writer.Write(output, graph) |> Phase3Fixtures.expectOk
        Assert.True(output.CanWrite)
        output.WriteByte(0uy)

        let canonical = Phase3Fixtures.bytes graph
        use input = new MemoryStream(canonical, false)
        let decoded = ArcIRJson.Reader.Read input |> Phase3Fixtures.expectOk

        Assert.True(input.CanRead)
        Assert.True((graph = decoded))

    [<Fact>]
    member _.``strict decoder rejects version shape identity and member errors``() =
        let emptyGraph = "\"graph\":{\"terms\":{},\"objects\":{},\"relations\":{}}"

        [ "arcir.json.unsupported-major", sprintf "{\"formatVersion\":\"2.0\",%s}" emptyGraph
          "arcir.json.unsupported-version", sprintf "{\"formatVersion\":\"1.1\",%s}" emptyGraph
          "arcir.json.unsupported-version", sprintf "{\"formatVersion\":\"01.00\",%s}" emptyGraph
          "arcir.json.invalid-version", sprintf "{\"formatVersion\":\"1\",%s}" emptyGraph
          "arcir.json.duplicate-member",
          sprintf "{\"formatVersion\":\"1.0\",\"formatVersion\":\"1.0\",%s}" emptyGraph
          "arcir.json.unknown-member",
          "{\"formatVersion\":\"1.0\",\"history\":[],\"graph\":{\"terms\":{},\"objects\":{},\"relations\":{}}}"
          "arcir.json.missing-member", "{\"formatVersion\":\"1.0\",\"graph\":{\"terms\":{},\"objects\":{}}}"
          "arcir.json.invalid-iri",
          "{\"formatVersion\":\"1.0\",\"graph\":{\"terms\":{\"relative\":{\"name\":null,\"source\":null}},\"objects\":{},\"relations\":{}}}"
          "arcir.json.duplicate-member",
          "{\"formatVersion\":\"1.0\",\"graph\":{\"terms\":{\"urn:t\":{\"name\":null,\"source\":null},\"urn:t\":{\"name\":null,\"source\":null}},\"objects\":{},\"relations\":{}}}"
          "arcir.json.unknown-value-type",
          "{\"formatVersion\":\"1.0\",\"graph\":{\"terms\":{},\"objects\":{\"urn:o\":{\"kind\":\"collection\",\"types\":{},\"properties\":{\"urn:p\":{\"predicate\":\"urn:predicate\",\"value\":{\"type\":\"mystery\",\"value\":\"x\"},\"annotations\":{}}},\"annotations\":{}}},\"relations\":{}}}" ]
        |> List.iter (fun (code, json) ->
            ArcIRJson.readString json |> Phase3Fixtures.expectErrorCode code)

    [<Fact>]
    member _.``reader rejects noncanonical floats and malformed input strings``() =
        let canonical =
            Phase3Fixtures.specialFloatState 1.0
            |> ArcIRJson.writeString
            |> Phase3Fixtures.expectOk

        canonical.Replace("\"value\": \"1\"", "\"value\": \"1.0\"")
        |> ArcIRJson.readString
        |> Phase3Fixtures.expectErrorCode "arcir.json.invalid-number"

        let malformedUtf16 = System.String([| '\uD800' |])

        ArcIRJson.readString malformedUtf16
        |> Phase3Fixtures.expectErrorCode "arcir.json.invalid-string"

    [<Fact>]
    member _.``disposed caller streams return persistence errors``() =
        let bytes = Phase3Fixtures.simpleState "disposed" |> Phase3Fixtures.bytes
        let input = new MemoryStream(bytes, false)
        input.Dispose()

        ArcIRJson.read input
        |> Phase3Fixtures.expectErrorCode "arcir.json.read-failed"

        let selector = ArcIRJson.selector Phase3Fixtures.simpleLocation
        let fragmentInput = new MemoryStream(bytes, false)
        fragmentInput.Dispose()

        ArcIRJson.resolve selector fragmentInput
        |> Phase3Fixtures.expectErrorCode "arcir.json.read-failed"

    [<Fact>]
    member _.``all special IEEE float values preserve their exact canonical category``() =
        let cases =
            [ Double.NaN, "NaN"
              Double.PositiveInfinity, "Infinity"
              Double.NegativeInfinity, "-Infinity"
              BitConverter.Int64BitsToDouble(Int64.MinValue), "-0" ]

        for value, lexical in cases do
            let state = Phase3Fixtures.specialFloatState value
            let json = ArcIRJson.writeString state |> Phase3Fixtures.expectOk
            let decoded = ArcIRJson.readString json |> Phase3Fixtures.expectOk
            let objectId = Phase3Fixtures.iri "urn:phase3:float-object"
            let propertyId = Phase3Fixtures.iri "urn:phase3:float-assertion"

            let decodedValue =
                match decoded.Objects.[objectId].Properties.[propertyId].Value with
                | ArcValue.Float number -> number
                | other -> failwithf "Expected float, got %A" other

            Assert.Contains(sprintf "\"value\": \"%s\"" lexical, json)

            if Double.IsNaN value then
                Assert.True(Double.IsNaN decodedValue)
            else
                Assert.Equal(BitConverter.DoubleToInt64Bits value, BitConverter.DoubleToInt64Bits decodedValue)

    [<Fact>]
    member _.``writer rejects null strings and authoritative key mismatches``() =
        let nullState = Phase3Fixtures.simpleState null
        ArcIRJson.writeBytes nullState |> Phase3Fixtures.expectErrorCode "arcir.json.invalid-string"

        let objectId = Phase3Fixtures.iri "urn:phase3:expected"
        let carriedId = Phase3Fixtures.iri "urn:phase3:carried"
        let object' = ArcObject.create carriedId ArcObjectKind.Collection Seq.empty Seq.empty Seq.empty
        let mismatch = { ArcIR.Empty with Objects = Map.ofList [ objectId, object' ] }

        ArcIRJson.writeBytes mismatch
        |> Phase3Fixtures.expectErrorCode "arcir.json.identity-key-mismatch"

    [<Fact>]
    member _.``referential validation findings remain reversibly serializable``() =
        let relationId = Phase3Fixtures.iri "urn:phase3:invalid-relation"
        let subject = Phase3Fixtures.iri "urn:phase3:missing-subject"
        let target = Phase3Fixtures.iri "urn:phase3:missing-target"
        let predicate = Phase3Fixtures.iri "urn:phase3:missing-predicate"
        let relation = ArcRelation.create relationId subject predicate target Seq.empty Seq.empty
        let graph = { ArcIR.Empty with Relations = Map.ofList [ relationId, relation ] }

        Assert.NotEmpty(Validation.validate graph)
        Assert.True((graph = (graph |> Phase3Fixtures.bytes |> Phase3Fixtures.readBytes)))
