namespace BioFSharp.ArcIR

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json

/// A typed location in the canonical ArcIR JSON representation.
[<RequireQualifiedAccess>]
type ArcJsonLocation =
    /// A graph-level ontology-term definition.
    | Term of termId: Iri
    /// A graph object.
    | Object of objectId: Iri
    /// A type assertion on an object.
    | TypeAssertion of objectId: Iri * assertionId: Iri
    /// A property assertion on an object.
    | Property of objectId: Iri * assertionId: Iri
    /// The atomic value occurrence of an object property assertion.
    | PropertyValue of objectId: Iri * assertionId: Iri
    /// An annotation assertion attached directly to an object.
    | ObjectAnnotation of objectId: Iri * annotationId: Iri
    /// The value occurrence of an annotation attached directly to an object.
    | ObjectAnnotationValue of objectId: Iri * annotationId: Iri
    /// An annotation assertion attached to an object property.
    | PropertyAnnotation of objectId: Iri * assertionId: Iri * annotationId: Iri
    /// The value occurrence of an annotation attached to an object property.
    | PropertyAnnotationValue of objectId: Iri * assertionId: Iri * annotationId: Iri
    /// A directed graph relation.
    | Relation of relationId: Iri
    /// A property assertion on a relation.
    | RelationProperty of relationId: Iri * assertionId: Iri
    /// The atomic value occurrence of a relation property assertion.
    | RelationPropertyValue of relationId: Iri * assertionId: Iri
    /// An annotation assertion attached to a relation property.
    | RelationPropertyAnnotation of relationId: Iri * assertionId: Iri * annotationId: Iri
    /// The value occurrence of an annotation attached to a relation property.
    | RelationPropertyAnnotationValue of relationId: Iri * assertionId: Iri * annotationId: Iri
    /// An annotation assertion attached directly to a relation.
    | RelationAnnotation of relationId: Iri * annotationId: Iri
    /// The value occurrence of an annotation attached directly to a relation.
    | RelationAnnotationValue of relationId: Iri * annotationId: Iri

module private ArcIRJsonInternal =

    exception DecodeFailure of PersistenceError

    [<Literal>]
    let FormatVersion = "1.0"

    let error code message =
        { Code = code
          Message = message }

    let fail code message = raise (DecodeFailure(error code message))

    let pathChild (path: string) (child: string) =
        if String.IsNullOrEmpty path then child else path + "/" + child

    let isWellFormedUtf16 (value: string) =
        if isNull value then
            false
        else
            let mutable index = 0
            let mutable valid = true

            while valid && index < value.Length do
                let character = value.[index]

                if Char.IsHighSurrogate character then
                    if index + 1 >= value.Length || not (Char.IsLowSurrogate value.[index + 1]) then
                        valid <- false
                    else
                        index <- index + 2
                elif Char.IsLowSurrogate character then
                    valid <- false
                else
                    index <- index + 1

            valid

    let identityError (container: string) (key: Iri) (valueId: Iri) =
        error
            "arcir.json.identity-key-mismatch"
            (sprintf "Map key '%s' does not match the contained identity '%s' at %s." key.Value valueId.Value container)

    let nullStringError (path: string) =
        error "arcir.json.invalid-string" (sprintf "String value at %s is null or contains malformed UTF-16." path)

    let rec valueStringErrors (path: string) (value: ArcValue) =
        seq {
            match value with
            | ArcValue.String text when not (isWellFormedUtf16 text) -> yield nullStringError path
            | ArcValue.List values ->
                for index, item in values |> List.indexed do
                    yield! valueStringErrors (sprintf "%s/%d" path index) item
            | _ -> ()
        }

    let annotationStringErrors (path: string) (annotation: ArcAnnotation) =
        seq {
            match annotation.Value with
            | AnnotationValue.Literal value -> yield! valueStringErrors (pathChild path "value") value
            | AnnotationValue.LiteralWithUnit(value, _) -> yield! valueStringErrors (pathChild path "value") value
            | AnnotationValue.Term _
            | AnnotationValue.TermWithUnit _ -> ()
        }

    let propertyErrors (ownerPath: string) (key: Iri) (property: ArcProperty) =
        seq {
            let path = pathChild ownerPath key.Value

            if key <> property.Id then
                yield identityError path key property.Id

            yield! valueStringErrors (pathChild path "value") property.Value

            for KeyValue(annotationKey, annotation) in property.Annotations do
                let annotationPath = pathChild (pathChild path "annotations") annotationKey.Value

                if annotationKey <> annotation.Id then
                    yield identityError annotationPath annotationKey annotation.Id

                yield! annotationStringErrors annotationPath annotation
        }

    let serializabilityErrors (ir: ArcIR) =
        [
            for KeyValue(termId, term) in ir.Terms do
                match term.Name with
                | Some name when not (isWellFormedUtf16 name) ->
                    nullStringError (sprintf "graph/terms/%s/name" termId.Value)
                | _ -> ()

                match term.Source with
                | Some source when not (isWellFormedUtf16 source) ->
                    nullStringError (sprintf "graph/terms/%s/source" termId.Value)
                | _ -> ()

            for KeyValue(objectKey, object') in ir.Objects do
                let objectPath = sprintf "graph/objects/%s" objectKey.Value

                if objectKey <> object'.Id then
                    identityError objectPath objectKey object'.Id

                for KeyValue(assertionKey, assertion) in object'.Types do
                    if assertionKey <> assertion.Id then
                        identityError (pathChild (pathChild objectPath "types") assertionKey.Value) assertionKey assertion.Id

                for KeyValue(propertyKey, property) in object'.Properties do
                    yield! propertyErrors (pathChild objectPath "properties") propertyKey property

                for KeyValue(annotationKey, annotation) in object'.Annotations do
                    let annotationPath = pathChild (pathChild objectPath "annotations") annotationKey.Value

                    if annotationKey <> annotation.Id then
                        identityError annotationPath annotationKey annotation.Id

                    yield! annotationStringErrors annotationPath annotation

            for KeyValue(relationKey, relation) in ir.Relations do
                let relationPath = sprintf "graph/relations/%s" relationKey.Value

                if relationKey <> relation.Id then
                    identityError relationPath relationKey relation.Id

                for KeyValue(propertyKey, property) in relation.Properties do
                    yield! propertyErrors (pathChild relationPath "properties") propertyKey property

                for KeyValue(annotationKey, annotation) in relation.Annotations do
                    let annotationPath = pathChild (pathChild relationPath "annotations") annotationKey.Value

                    if annotationKey <> annotation.Id then
                        identityError annotationPath annotationKey annotation.Id

                    yield! annotationStringErrors annotationPath annotation
        ]

    let orderedEntries (values: Map<Iri, 'value>) =
        values
        |> Map.toSeq
        |> Seq.sortWith (fun ((left: Iri), _) ((right: Iri), _) -> StringComparer.Ordinal.Compare(left.Value, right.Value))

    let writeOptionalString (writer: Utf8JsonWriter) (name: string) (value: string option) =
        match value with
        | Some text -> writer.WriteString(name, text)
        | None -> writer.WriteNull(name)

    let writeOptionalIri (writer: Utf8JsonWriter) (name: string) (value: Iri option) =
        match value with
        | Some iri -> writer.WriteString(name, iri.Value)
        | None -> writer.WriteNull(name)

    let writeMap
        (writer: Utf8JsonWriter)
        (name: string)
        (values: Map<Iri, 'value>)
        (writeValue: Utf8JsonWriter -> 'value -> unit)
        =
        writer.WritePropertyName(name)
        writer.WriteStartObject()

        for key, value in orderedEntries values do
            writer.WritePropertyName(key.Value)
            writeValue writer value

        writer.WriteEndObject()

    let canonicalFloat (value: float) =
        if Double.IsNaN value then
            "NaN"
        elif Double.IsPositiveInfinity value then
            "Infinity"
        elif Double.IsNegativeInfinity value then
            "-Infinity"
        elif BitConverter.DoubleToInt64Bits value = Int64.MinValue then
            "-0"
        else
            value.ToString("G17", CultureInfo.InvariantCulture)

    let rec writeArcValue (writer: Utf8JsonWriter) value =
        writer.WriteStartObject()

        match value with
        | ArcValue.String text ->
            writer.WriteString("type", "string")
            writer.WriteString("value", text)
        | ArcValue.Integer number ->
            writer.WriteString("type", "integer")
            writer.WriteNumber("value", number)
        | ArcValue.Float number ->
            writer.WriteString("type", "float")
            writer.WriteString("value", canonicalFloat number)
        | ArcValue.Boolean boolean ->
            writer.WriteString("type", "boolean")
            writer.WriteBoolean("value", boolean)
        | ArcValue.DateTime dateTime ->
            writer.WriteString("type", "dateTime")
            writer.WriteString("value", dateTime.ToString("O", CultureInfo.InvariantCulture))
        | ArcValue.Iri iri ->
            writer.WriteString("type", "iri")
            writer.WriteString("value", iri.Value)
        | ArcValue.Ref reference ->
            writer.WriteString("type", "ref")
            writer.WriteString("value", reference.Value)
        | ArcValue.List values ->
            writer.WriteString("type", "list")
            writer.WritePropertyName("value")
            writer.WriteStartArray()
            values |> List.iter (writeArcValue writer)
            writer.WriteEndArray()

        writer.WriteEndObject()

    let writeAnnotationValue (writer: Utf8JsonWriter) value =
        writer.WriteStartObject()

        match value with
        | AnnotationValue.Literal literal ->
            writer.WriteString("type", "literal")
            writer.WritePropertyName("value")
            writeArcValue writer literal
        | AnnotationValue.Term term ->
            writer.WriteString("type", "term")
            writer.WriteString("value", term.Value)
        | AnnotationValue.LiteralWithUnit(literal, unit) ->
            writer.WriteString("type", "literalWithUnit")
            writer.WritePropertyName("value")
            writeArcValue writer literal
            writer.WriteString("unit", unit.Value)
        | AnnotationValue.TermWithUnit(term, unit) ->
            writer.WriteString("type", "termWithUnit")
            writer.WriteString("value", term.Value)
            writer.WriteString("unit", unit.Value)

        writer.WriteEndObject()

    let writeAnnotation (writer: Utf8JsonWriter) (annotation: ArcAnnotation) =
        writer.WriteStartObject()
        writer.WriteString("property", annotation.Property.Value)
        writer.WritePropertyName("value")
        writeAnnotationValue writer annotation.Value
        writeOptionalIri writer "evidence" annotation.Evidence
        writeOptionalIri writer "source" annotation.Source
        writer.WriteEndObject()

    let writeProperty (writer: Utf8JsonWriter) (property: ArcProperty) =
        writer.WriteStartObject()
        writer.WriteString("predicate", property.Predicate.Value)
        writer.WritePropertyName("value")
        writeArcValue writer property.Value
        writeMap writer "annotations" property.Annotations writeAnnotation
        writer.WriteEndObject()

    let writeTypeAssertion (writer: Utf8JsonWriter) (assertion: ArcTypeAssertion) =
        writer.WriteStartObject()
        writer.WriteString("term", assertion.Term.Value)
        writer.WriteEndObject()

    let writeObject (writer: Utf8JsonWriter) (object': ArcObject) =
        let kind =
            match object'.Kind with
            | ArcObjectKind.Observable -> "observable"
            | ArcObjectKind.Instrument -> "instrument"
            | ArcObjectKind.Resource -> "resource"
            | ArcObjectKind.Activity -> "activity"
            | ArcObjectKind.Agent -> "agent"
            | ArcObjectKind.Role -> "role"
            | ArcObjectKind.Recipe -> "recipe"
            | ArcObjectKind.Collection -> "collection"
            | ArcObjectKind.Selector -> "selector"

        writer.WriteStartObject()
        writer.WriteString("kind", kind)
        writeMap writer "types" object'.Types writeTypeAssertion
        writeMap writer "properties" object'.Properties writeProperty
        writeMap writer "annotations" object'.Annotations writeAnnotation
        writer.WriteEndObject()

    let writeTerm (writer: Utf8JsonWriter) (term: OntologyTerm) =
        writer.WriteStartObject()
        writeOptionalString writer "name" term.Name
        writeOptionalString writer "source" term.Source
        writer.WriteEndObject()

    let writeRelation (writer: Utf8JsonWriter) (relation: ArcRelation) =
        writer.WriteStartObject()
        writer.WriteString("subject", relation.Subject.Value)
        writer.WriteString("predicate", relation.Predicate.Value)
        writer.WriteString("object", relation.Object.Value)
        writeMap writer "properties" relation.Properties writeProperty
        writeMap writer "annotations" relation.Annotations writeAnnotation
        writer.WriteEndObject()

    let serialize (ir: ArcIR) =
        match serializabilityErrors ir with
        | errors when not (List.isEmpty errors) -> Error errors
        | _ ->
            try
                use stream = new MemoryStream()
                let mutable options = JsonWriterOptions()
                options.Indented <- true
                options.Encoder <- JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                options.IndentCharacter <- ' '
                options.IndentSize <- 2
                options.NewLine <- "\n"
                options.MaxDepth <- 1024

                use writer = new Utf8JsonWriter(stream, options)
                writer.WriteStartObject()
                writer.WriteString("formatVersion", FormatVersion)
                writer.WritePropertyName("graph")
                writer.WriteStartObject()
                writeMap writer "terms" ir.Terms writeTerm
                writeMap writer "objects" ir.Objects writeObject
                writeMap writer "relations" ir.Relations writeRelation
                writer.WriteEndObject()
                writer.WriteEndObject()
                writer.Flush()

                let withoutNewline = stream.ToArray()
                let bytes = Array.zeroCreate<byte> (withoutNewline.Length + 1)
                Array.Copy(withoutNewline, bytes, withoutNewline.Length)
                bytes.[bytes.Length - 1] <- byte '\n'
                Ok bytes
            with ex ->
                Error [ error "arcir.json.write-failed" (sprintf "Canonical JSON serialization failed: %s" ex.Message) ]

    let collectProperties (path: string) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            fail "arcir.json.invalid-shape" (sprintf "Expected a JSON object at %s." path)

        let properties = Dictionary<string, JsonElement>(StringComparer.Ordinal)

        for property in element.EnumerateObject() do
            if properties.ContainsKey property.Name then
                fail
                    "arcir.json.duplicate-member"
                    (sprintf "JSON object at %s contains duplicate member '%s'." path property.Name)

            properties.Add(property.Name, property.Value)

        properties

    let recordProperties (path: string) (allowed: string list) (required: string list) (element: JsonElement) =
        let properties = collectProperties path element
        let allowed = Set.ofList allowed

        for name in properties.Keys do
            if not (Set.contains name allowed) then
                fail "arcir.json.unknown-member" (sprintf "JSON object at %s contains unknown member '%s'." path name)

        for name in required do
            if not (properties.ContainsKey name) then
                fail "arcir.json.missing-member" (sprintf "JSON object at %s is missing required member '%s'." path name)

        properties

    let property (path: string) (name: string) (properties: Dictionary<string, JsonElement>) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if properties.TryGetValue(name, &value) then
            value
        else
            fail "arcir.json.missing-member" (sprintf "JSON object at %s is missing required member '%s'." path name)

    let readString (path: string) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.String then
            fail "arcir.json.invalid-shape" (sprintf "Expected a JSON string at %s." path)

        let value = element.GetString()

        if not (isWellFormedUtf16 value) then
            fail "arcir.json.invalid-string" (sprintf "String at %s is null or contains malformed UTF-16." path)

        value

    let readNullableString (path: string) (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.String -> Some(readString path element)
        | _ -> fail "arcir.json.invalid-shape" (sprintf "Expected a string or null at %s." path)

    let readIri (path: string) (element: JsonElement) =
        let value = readString path element

        match Iri.TryCreate value with
        | Some iri -> iri
        | None -> fail "arcir.json.invalid-iri" (sprintf "Value '%s' at %s is not an absolute IRI." value path)

    let readNullableIri (path: string) (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.String -> Some(readIri path element)
        | _ -> fail "arcir.json.invalid-shape" (sprintf "Expected an IRI string or null at %s." path)

    let readInt64 path (element: JsonElement) =
        let mutable value = 0L

        if element.ValueKind = JsonValueKind.Number && element.TryGetInt64(&value) then
            value
        else
            fail "arcir.json.invalid-number" (sprintf "Expected a signed 64-bit integer at %s." path)

    let readBoolean path (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.True -> true
        | JsonValueKind.False -> false
        | _ -> fail "arcir.json.invalid-shape" (sprintf "Expected a Boolean at %s." path)

    let readFloat (path: string) (element: JsonElement) =
        let value = readString path element

        match value with
        | "NaN" -> Double.NaN
        | "Infinity" -> Double.PositiveInfinity
        | "-Infinity" -> Double.NegativeInfinity
        | "-0" -> BitConverter.Int64BitsToDouble(Int64.MinValue)
        | _ ->
            let mutable number = 0.0

            if Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, &number)
               && String.Equals(value, canonicalFloat number, StringComparison.Ordinal) then
                number
            else
                fail "arcir.json.invalid-number" (sprintf "Value '%s' at %s is not a canonical invariant float." value path)

    let readDateTime (path: string) (element: JsonElement) =
        let value = readString path element
        let mutable dateTime = DateTimeOffset.MinValue

        if DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, &dateTime) then
            dateTime
        else
            fail "arcir.json.invalid-date-time" (sprintf "Value '%s' at %s is not a round-trip timestamp." value path)

    let rec readArcValue path element =
        let properties = recordProperties path [ "type"; "value" ] [ "type"; "value" ] element
        let valueType = readString (pathChild path "type") (property path "type" properties)
        let valueElement = property path "value" properties

        match valueType with
        | "string" -> ArcValue.String(readString (pathChild path "value") valueElement)
        | "integer" -> ArcValue.Integer(readInt64 (pathChild path "value") valueElement)
        | "float" -> ArcValue.Float(readFloat (pathChild path "value") valueElement)
        | "boolean" -> ArcValue.Boolean(readBoolean (pathChild path "value") valueElement)
        | "dateTime" -> ArcValue.DateTime(readDateTime (pathChild path "value") valueElement)
        | "iri" -> ArcValue.Iri(readIri (pathChild path "value") valueElement)
        | "ref" -> ArcValue.Ref(readIri (pathChild path "value") valueElement)
        | "list" ->
            if valueElement.ValueKind <> JsonValueKind.Array then
                fail "arcir.json.invalid-shape" (sprintf "Expected a JSON array at %s/value." path)

            valueElement.EnumerateArray()
            |> Seq.mapi (fun index item -> readArcValue (sprintf "%s/value/%d" path index) item)
            |> List.ofSeq
            |> ArcValue.List
        | unknown -> fail "arcir.json.unknown-value-type" (sprintf "Unknown ArcValue type '%s' at %s." unknown path)

    let readAnnotationValue path element =
        let initial = collectProperties path element
        let valueType = readString (pathChild path "type") (property path "type" initial)

        match valueType with
        | "literal" ->
            let properties = recordProperties path [ "type"; "value" ] [ "type"; "value" ] element
            AnnotationValue.Literal(readArcValue (pathChild path "value") (property path "value" properties))
        | "term" ->
            let properties = recordProperties path [ "type"; "value" ] [ "type"; "value" ] element
            AnnotationValue.Term(readIri (pathChild path "value") (property path "value" properties))
        | "literalWithUnit" ->
            let properties =
                recordProperties path [ "type"; "value"; "unit" ] [ "type"; "value"; "unit" ] element

            AnnotationValue.LiteralWithUnit(
                readArcValue (pathChild path "value") (property path "value" properties),
                readIri (pathChild path "unit") (property path "unit" properties)
            )
        | "termWithUnit" ->
            let properties =
                recordProperties path [ "type"; "value"; "unit" ] [ "type"; "value"; "unit" ] element

            AnnotationValue.TermWithUnit(
                readIri (pathChild path "value") (property path "value" properties),
                readIri (pathChild path "unit") (property path "unit" properties)
            )
        | unknown ->
            fail "arcir.json.unknown-annotation-value-type" (sprintf "Unknown AnnotationValue type '%s' at %s." unknown path)

    let readMap path readValue element =
        let properties = collectProperties path element

        properties
        |> Seq.map (fun (KeyValue(key, value)) ->
            let keyPath = pathChild path key

            let iri =
                match Iri.TryCreate key with
                | Some iri -> iri
                | None -> fail "arcir.json.invalid-iri" (sprintf "Map key '%s' at %s is not an absolute IRI." key path)

            iri, readValue keyPath iri value)
        |> Map.ofSeq

    let readTerm path _ element =
        let properties = recordProperties path [ "name"; "source" ] [ "name"; "source" ] element

        { Name = readNullableString (pathChild path "name") (property path "name" properties)
          Source = readNullableString (pathChild path "source") (property path "source" properties) }

    let readTypeAssertion path id element =
        let properties = recordProperties path [ "term" ] [ "term" ] element

        { Id = id
          Term = readIri (pathChild path "term") (property path "term" properties) }

    let readAnnotation path id element =
        let properties =
            recordProperties
                path
                [ "property"; "value"; "evidence"; "source" ]
                [ "property"; "value"; "evidence"; "source" ]
                element

        { Id = id
          Property = readIri (pathChild path "property") (property path "property" properties)
          Value = readAnnotationValue (pathChild path "value") (property path "value" properties)
          Evidence = readNullableIri (pathChild path "evidence") (property path "evidence" properties)
          Source = readNullableIri (pathChild path "source") (property path "source" properties) }

    let readProperty path id element =
        let properties =
            recordProperties path [ "predicate"; "value"; "annotations" ] [ "predicate"; "value"; "annotations" ] element

        { Id = id
          Predicate = readIri (pathChild path "predicate") (property path "predicate" properties)
          Value = readArcValue (pathChild path "value") (property path "value" properties)
          Annotations =
            readMap
                (pathChild path "annotations")
                readAnnotation
                (property path "annotations" properties) }

    let readObjectKind path element =
        match readString path element with
        | "observable" -> ArcObjectKind.Observable
        | "instrument" -> ArcObjectKind.Instrument
        | "resource" -> ArcObjectKind.Resource
        | "activity" -> ArcObjectKind.Activity
        | "agent" -> ArcObjectKind.Agent
        | "role" -> ArcObjectKind.Role
        | "recipe" -> ArcObjectKind.Recipe
        | "collection" -> ArcObjectKind.Collection
        | "selector" -> ArcObjectKind.Selector
        | unknown -> fail "arcir.json.unknown-object-kind" (sprintf "Unknown ArcObjectKind '%s' at %s." unknown path)

    let readObject path id element =
        let properties =
            recordProperties
                path
                [ "kind"; "types"; "properties"; "annotations" ]
                [ "kind"; "types"; "properties"; "annotations" ]
                element

        { Id = id
          Kind = readObjectKind (pathChild path "kind") (property path "kind" properties)
          Types = readMap (pathChild path "types") readTypeAssertion (property path "types" properties)
          Properties = readMap (pathChild path "properties") readProperty (property path "properties" properties)
          Annotations = readMap (pathChild path "annotations") readAnnotation (property path "annotations" properties) }

    let readRelation path id element =
        let properties =
            recordProperties
                path
                [ "subject"; "predicate"; "object"; "properties"; "annotations" ]
                [ "subject"; "predicate"; "object"; "properties"; "annotations" ]
                element

        { Id = id
          Subject = readIri (pathChild path "subject") (property path "subject" properties)
          Predicate = readIri (pathChild path "predicate") (property path "predicate" properties)
          Object = readIri (pathChild path "object") (property path "object" properties)
          Properties = readMap (pathChild path "properties") readProperty (property path "properties" properties)
          Annotations = readMap (pathChild path "annotations") readAnnotation (property path "annotations" properties) }

    let readFormatVersion path element =
        let value = readString path element
        let parts = value.Split('.')
        let mutable major = 0
        let mutable minor = 0

        if parts.Length <> 2
           || not (Int32.TryParse(parts.[0], NumberStyles.None, CultureInfo.InvariantCulture, &major))
           || not (Int32.TryParse(parts.[1], NumberStyles.None, CultureInfo.InvariantCulture, &minor)) then
            fail "arcir.json.invalid-version" (sprintf "Format version '%s' is not a major.minor version." value)

        if major <> 1 then
            fail "arcir.json.unsupported-major" (sprintf "ArcIR JSON major version %d is not supported." major)

        if not (String.Equals(value, FormatVersion, StringComparison.Ordinal)) then
            fail "arcir.json.unsupported-version" (sprintf "ArcIR JSON version '%s' is not supported." value)

    let decodeRoot (root: JsonElement) =
        let rootProperties =
            recordProperties "$" [ "formatVersion"; "graph" ] [ "formatVersion"; "graph" ] root

        readFormatVersion "$/formatVersion" (property "$" "formatVersion" rootProperties)

        let graphElement = property "$" "graph" rootProperties
        let graphProperties =
            recordProperties
                "$/graph"
                [ "terms"; "objects"; "relations" ]
                [ "terms"; "objects"; "relations" ]
                graphElement

        { Terms = readMap "$/graph/terms" readTerm (property "$/graph" "terms" graphProperties)
          Objects = readMap "$/graph/objects" readObject (property "$/graph" "objects" graphProperties)
          Relations = readMap "$/graph/relations" readRelation (property "$/graph" "relations" graphProperties) }

    let readDocument (stream: Stream) =
        try
            let mutable options = JsonDocumentOptions()
            options.AllowTrailingCommas <- false
            options.CommentHandling <- JsonCommentHandling.Disallow
            options.MaxDepth <- 1024

            use document = JsonDocument.Parse(stream, options)
            Ok(decodeRoot document.RootElement)
        with
        | DecodeFailure decodeError -> Error [ decodeError ]
        | :? JsonException as ex -> Error [ error "arcir.json.invalid-json" ex.Message ]
        | :? IOException as ex -> Error [ error "arcir.json.read-failed" ex.Message ]
        | :? UnauthorizedAccessException as ex -> Error [ error "arcir.json.read-failed" ex.Message ]
        | :? ArgumentException as ex -> Error [ error "arcir.json.read-failed" ex.Message ]
        | :? NotSupportedException as ex -> Error [ error "arcir.json.read-failed" ex.Message ]
        | :? ObjectDisposedException as ex -> Error [ error "arcir.json.read-failed" ex.Message ]

    let isHex character =
        (character >= '0' && character <= '9')
        || (character >= 'a' && character <= 'f')
        || (character >= 'A' && character <= 'F')

    let hexValue character =
        if character >= '0' && character <= '9' then int character - int '0'
        elif character >= 'a' && character <= 'f' then int character - int 'a' + 10
        else int character - int 'A' + 10

    let isRawFragmentCharacter character =
        (character >= 'a' && character <= 'z')
        || (character >= 'A' && character <= 'Z')
        || (character >= '0' && character <= '9')
        || "-._~!$&'()*+,;=:@/?".IndexOf(character) >= 0

    let percentDecode (value: string) =
        try
            let bytes = ResizeArray<byte>()
            let strictUtf8 = UTF8Encoding(false, true)
            let mutable index = 0

            while index < value.Length do
                let character = value.[index]

                if character = '%' then
                    if index + 2 >= value.Length || not (isHex value.[index + 1]) || not (isHex value.[index + 2]) then
                        fail "arcir.json.invalid-selector" "JSON Pointer contains a malformed percent escape."

                    bytes.Add(byte ((hexValue value.[index + 1] <<< 4) ||| hexValue value.[index + 2]))
                    index <- index + 3
                elif int character <= 0x7F && isRawFragmentCharacter character then
                    bytes.Add(byte character)
                    index <- index + 1
                else
                    fail
                        "arcir.json.invalid-selector"
                        "JSON Pointer URI fragments must percent-encode non-ASCII and fragment-disallowed characters."

            strictUtf8.GetString(bytes.ToArray())
        with
        | DecodeFailure _ as ex -> raise ex
        | :? EncoderFallbackException
        | :? DecoderFallbackException ->
            fail "arcir.json.invalid-selector" "JSON Pointer contains invalid UTF-8 or malformed Unicode."

    let decodePointerToken (token: string) =
        let result = StringBuilder()
        let mutable index = 0

        while index < token.Length do
            if token.[index] = '~' then
                if index + 1 >= token.Length then
                    fail "arcir.json.invalid-selector" "JSON Pointer ends with an incomplete '~' escape."

                match token.[index + 1] with
                | '0' -> result.Append('~') |> ignore
                | '1' -> result.Append('/') |> ignore
                | invalid ->
                    fail
                        "arcir.json.invalid-selector"
                        (sprintf "JSON Pointer contains invalid escape '~%c'." invalid)

                index <- index + 2
            else
                result.Append(token.[index]) |> ignore
                index <- index + 1

        result.ToString()

    let pointerTokens (value: string) =
        if isNull value || not (value.StartsWith("#", StringComparison.Ordinal)) then
            fail "arcir.json.invalid-selector" "ArcIR JSON selectors must use RFC 6901 URI-fragment form beginning with '#'."

        let pointer = percentDecode (value.Substring(1))

        if String.IsNullOrEmpty pointer then
            [||]
        elif not (pointer.StartsWith("/", StringComparison.Ordinal)) then
            fail "arcir.json.invalid-selector" "A non-empty JSON Pointer must begin with '/'."
        else
            pointer.Substring(1).Split([| '/' |], StringSplitOptions.None)
            |> Array.map decodePointerToken

    let ensureUniqueMembers path (element: JsonElement) =
        let names = HashSet<string>(StringComparer.Ordinal)

        for item in element.EnumerateObject() do
            if not (names.Add item.Name) then
                fail
                    "arcir.json.duplicate-member"
                    (sprintf "JSON object at %s contains duplicate member '%s'." path item.Name)

    let findObjectMember path token (element: JsonElement) =
        ensureUniqueMembers path element
        let mutable found = false
        let mutable value = Unchecked.defaultof<JsonElement>

        for item in element.EnumerateObject() do
            if String.Equals(item.Name, token, StringComparison.Ordinal) then
                found <- true
                value <- item.Value

        if found then
            value
        else
            fail "arcir.json.fragment-not-found" (sprintf "JSON Pointer member '%s' does not exist at %s." token path)

    let readArrayIndex path token length =
        let mutable index = 0

        if token = "-"
           || String.IsNullOrEmpty token
           || (token.Length > 1 && token.[0] = '0')
           || not (Int32.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, &index))
           || index < 0
           || index >= length then
            fail "arcir.json.fragment-not-found" (sprintf "JSON Pointer array index '%s' does not exist at %s." token path)

        index

    let resolveElement (tokens: string array) (root: JsonElement) =
        ((root, "$"), tokens)
        ||> Array.fold (fun (current, path) token ->
            match current.ValueKind with
            | JsonValueKind.Object -> findObjectMember path token current, pathChild path token
            | JsonValueKind.Array ->
                let index = readArrayIndex path token (current.GetArrayLength())
                current.[index], sprintf "%s/%d" path index
            | _ ->
                fail
                    "arcir.json.fragment-not-found"
                    (sprintf "JSON Pointer cannot traverse scalar value at %s." path))
        |> fst

    let readJsonElement (selectorConformsTo: Iri) (selector: FragmentSelector) (stream: Stream) =
        if selector.ConformsTo <> selectorConformsTo then
            Error
                [ error
                      "arcir.json.unsupported-selector"
                      (sprintf "Selector conforms to '%s', not RFC 6901." selector.ConformsTo.Value) ]
        else
            try
                let tokens = pointerTokens selector.Value
                let mutable options = JsonDocumentOptions()
                options.AllowTrailingCommas <- false
                options.CommentHandling <- JsonCommentHandling.Disallow
                options.MaxDepth <- 1024

                use document = JsonDocument.Parse(stream, options)
                Ok((resolveElement tokens document.RootElement).Clone())
            with
            | DecodeFailure decodeError -> Error [ decodeError ]
            | :? JsonException as ex -> Error [ error "arcir.json.invalid-json" ex.Message ]
            | :? IOException as ex -> Error [ error "arcir.json.read-failed" ex.Message ]
            | :? UnauthorizedAccessException as ex -> Error [ error "arcir.json.read-failed" ex.Message ]
            | :? ArgumentException as ex -> Error [ error "arcir.json.read-failed" ex.Message ]
            | :? NotSupportedException as ex -> Error [ error "arcir.json.read-failed" ex.Message ]
            | :? ObjectDisposedException as ex -> Error [ error "arcir.json.read-failed" ex.Message ]

    let isFragmentAllowed (byteValue: byte) =
        let character = char byteValue

        (character >= 'a' && character <= 'z')
        || (character >= 'A' && character <= 'Z')
        || (character >= '0' && character <= '9')
        || "-._~!$&'()*+,;=:@?".IndexOf(character) >= 0

    let encodePointerToken (value: string) =
        let escaped = value.Replace("~", "~0").Replace("/", "~1")
        let bytes = UTF8Encoding(false, true).GetBytes escaped
        let result = StringBuilder()

        for byteValue in bytes do
            if byteValue <= 0x7Fuy && isFragmentAllowed byteValue then
                result.Append(char byteValue) |> ignore
            else
                result.Append('%').Append(byteValue.ToString("X2", CultureInfo.InvariantCulture)) |> ignore

        result.ToString()

    let pointer tokens =
        tokens
        |> List.map encodePointerToken
        |> String.concat "/"
        |> fun encoded -> "#/" + encoded

/// Deterministic canonical JSON persistence and addressing for ArcIR state artifacts.
[<RequireQualifiedAccess>]
module ArcIRJson =

    /// The current canonical ArcIR JSON format version.
    let FormatVersion = ArcIRJsonInternal.FormatVersion

    /// The JSON Schema identifier for canonical ArcIR JSON version 1.0.
    [<Literal>]
    let SchemaId = "urn:biofsharp:arcir:schema:1.0"

    /// The selector-conformance IRI used for RFC 6901 JSON Pointers.
    let JsonPointerConformsTo = Iri.Create "https://www.rfc-editor.org/rfc/rfc6901"

    /// Serializes a graph to deterministic UTF-8 JSON bytes without a byte-order mark.
    let writeBytes ir = ArcIRJsonInternal.serialize ir

    /// Serializes a graph to deterministic canonical JSON text ending in one line feed.
    let writeString ir =
        writeBytes ir |> Result.map Encoding.UTF8.GetString

    /// Writes canonical JSON to a caller-owned stream without closing it.
    let write (stream: Stream) ir =
        match writeBytes ir with
        | Error errors -> Error errors
        | Ok bytes ->
            try
                stream.Write(bytes, 0, bytes.Length)
                Ok()
            with
            | :? IOException as ex ->
                Error [ ArcIRJsonInternal.error "arcir.json.write-failed" ex.Message ]
            | :? UnauthorizedAccessException as ex ->
                Error [ ArcIRJsonInternal.error "arcir.json.write-failed" ex.Message ]
            | :? ArgumentException as ex ->
                Error [ ArcIRJsonInternal.error "arcir.json.write-failed" ex.Message ]
            | :? NotSupportedException as ex ->
                Error [ ArcIRJsonInternal.error "arcir.json.write-failed" ex.Message ]
            | :? ObjectDisposedException as ex ->
                Error [ ArcIRJsonInternal.error "arcir.json.write-failed" ex.Message ]

    /// Reads canonical ArcIR JSON from a caller-owned stream without closing it.
    let read (stream: Stream) = ArcIRJsonInternal.readDocument stream

    /// Reads canonical ArcIR JSON text.
    let readString (json: string) =
        if isNull json then
            Error [ ArcIRJsonInternal.error "arcir.json.invalid-string" "ArcIR JSON text cannot be null." ]
        else
            try
                let bytes = UTF8Encoding(false, true).GetBytes json
                use stream = new MemoryStream(bytes, false)
                read stream
            with :? EncoderFallbackException as ex ->
                Error [ ArcIRJsonInternal.error "arcir.json.invalid-string" ex.Message ]

    /// Reads a canonical ArcIR state from a file without digest verification.
    let readFile path =
        try
            use stream = File.OpenRead path
            read stream
        with
        | :? IOException as ex -> Error [ ArcIRJsonInternal.error "arcir.json.read-failed" ex.Message ]
        | :? UnauthorizedAccessException as ex -> Error [ ArcIRJsonInternal.error "arcir.json.read-failed" ex.Message ]
        | :? ArgumentException as ex -> Error [ ArcIRJsonInternal.error "arcir.json.read-failed" ex.Message ]
        | :? NotSupportedException as ex -> Error [ ArcIRJsonInternal.error "arcir.json.read-failed" ex.Message ]

    /// Atomically publishes a new immutable state file and returns its exact byte revision.
    let writeNew path ir =
        if String.IsNullOrWhiteSpace path then
            Error [ ArcIRJsonInternal.error "arcir.json.invalid-path" "State artifact path cannot be empty." ]
        else
            match writeBytes ir with
            | Error errors -> Error errors
            | Ok bytes ->
                let mutable temporaryPath = None

                try
                    try
                        let directory = Path.GetDirectoryName path
                        let fileName = Path.GetFileName path

                        if String.IsNullOrWhiteSpace fileName then
                            Error
                                [ ArcIRJsonInternal.error
                                      "arcir.json.invalid-path"
                                      (sprintf "State artifact path '%s' does not name a file." path) ]
                        elif File.Exists path then
                            Error
                                [ ArcIRJsonInternal.error
                                      "arcir.json.state-exists"
                                      (sprintf "Immutable state artifact '%s' already exists." path) ]
                        else
                            if not (String.IsNullOrEmpty directory) then
                                Directory.CreateDirectory directory |> ignore

                            let temporaryName = sprintf ".%s.%s.tmp" fileName (Guid.NewGuid().ToString("N"))
                            let temporary =
                                if String.IsNullOrEmpty directory then temporaryName else Path.Combine(directory, temporaryName)

                            temporaryPath <- Some temporary

                            do
                                use output =
                                    new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)

                                output.Write(bytes, 0, bytes.Length)
                                output.Flush(true)

                            File.Move(temporary, path)
                            temporaryPath <- None
                            Ok(ArtifactRevision.ofBytes path None bytes)
                    with
                    | :? IOException as ex ->
                        let code = if File.Exists path then "arcir.json.state-exists" else "arcir.json.write-failed"
                        Error [ ArcIRJsonInternal.error code ex.Message ]
                    | :? UnauthorizedAccessException as ex ->
                        Error [ ArcIRJsonInternal.error "arcir.json.write-failed" ex.Message ]
                    | :? ArgumentException as ex ->
                        Error [ ArcIRJsonInternal.error "arcir.json.invalid-path" ex.Message ]
                    | :? NotSupportedException as ex ->
                        Error [ ArcIRJsonInternal.error "arcir.json.invalid-path" ex.Message ]
                finally
                    match temporaryPath with
                    | Some temporary when File.Exists temporary ->
                        try
                            File.Delete temporary
                        with _ ->
                            ()
                    | _ -> ()

    /// Reads an artifact only when its exact bytes match the declared SHA-256 digest.
    let readRevision (revision: ArtifactRevision) =
        try
            let bytes = File.ReadAllBytes revision.Path

            if ArtifactRevision.verifyBytes revision bytes then
                use stream = new MemoryStream(bytes, false)
                read stream
            else
                Error
                    [ ArcIRJsonInternal.error
                          "arcir.json.digest-mismatch"
                          (sprintf "Artifact '%s' does not match SHA-256 digest %s." revision.Path revision.Sha256) ]
        with
        | :? IOException as ex -> Error [ ArcIRJsonInternal.error "arcir.json.read-failed" ex.Message ]
        | :? UnauthorizedAccessException as ex -> Error [ ArcIRJsonInternal.error "arcir.json.read-failed" ex.Message ]
        | :? ArgumentException as ex -> Error [ ArcIRJsonInternal.error "arcir.json.read-failed" ex.Message ]
        | :? NotSupportedException as ex -> Error [ ArcIRJsonInternal.error "arcir.json.read-failed" ex.Message ]

    /// Converts a typed location to an RFC 6901 URI-fragment JSON Pointer.
    let selector location =
        let path =
            match location with
            | ArcJsonLocation.Term termId -> [ "graph"; "terms"; termId.Value ]
            | ArcJsonLocation.Object objectId -> [ "graph"; "objects"; objectId.Value ]
            | ArcJsonLocation.TypeAssertion(objectId, assertionId) ->
                [ "graph"; "objects"; objectId.Value; "types"; assertionId.Value ]
            | ArcJsonLocation.Property(objectId, assertionId) ->
                [ "graph"; "objects"; objectId.Value; "properties"; assertionId.Value ]
            | ArcJsonLocation.PropertyValue(objectId, assertionId) ->
                [ "graph"; "objects"; objectId.Value; "properties"; assertionId.Value; "value" ]
            | ArcJsonLocation.ObjectAnnotation(objectId, annotationId) ->
                [ "graph"; "objects"; objectId.Value; "annotations"; annotationId.Value ]
            | ArcJsonLocation.ObjectAnnotationValue(objectId, annotationId) ->
                [ "graph"; "objects"; objectId.Value; "annotations"; annotationId.Value; "value" ]
            | ArcJsonLocation.PropertyAnnotation(objectId, assertionId, annotationId) ->
                [ "graph"
                  "objects"
                  objectId.Value
                  "properties"
                  assertionId.Value
                  "annotations"
                  annotationId.Value ]
            | ArcJsonLocation.PropertyAnnotationValue(objectId, assertionId, annotationId) ->
                [ "graph"
                  "objects"
                  objectId.Value
                  "properties"
                  assertionId.Value
                  "annotations"
                  annotationId.Value
                  "value" ]
            | ArcJsonLocation.Relation relationId -> [ "graph"; "relations"; relationId.Value ]
            | ArcJsonLocation.RelationProperty(relationId, assertionId) ->
                [ "graph"; "relations"; relationId.Value; "properties"; assertionId.Value ]
            | ArcJsonLocation.RelationPropertyValue(relationId, assertionId) ->
                [ "graph"; "relations"; relationId.Value; "properties"; assertionId.Value; "value" ]
            | ArcJsonLocation.RelationPropertyAnnotation(relationId, assertionId, annotationId) ->
                [ "graph"
                  "relations"
                  relationId.Value
                  "properties"
                  assertionId.Value
                  "annotations"
                  annotationId.Value ]
            | ArcJsonLocation.RelationPropertyAnnotationValue(relationId, assertionId, annotationId) ->
                [ "graph"
                  "relations"
                  relationId.Value
                  "properties"
                  assertionId.Value
                  "annotations"
                  annotationId.Value
                  "value" ]
            | ArcJsonLocation.RelationAnnotation(relationId, annotationId) ->
                [ "graph"; "relations"; relationId.Value; "annotations"; annotationId.Value ]
            | ArcJsonLocation.RelationAnnotationValue(relationId, annotationId) ->
                [ "graph"; "relations"; relationId.Value; "annotations"; annotationId.Value; "value" ]

        { ConformsTo = JsonPointerConformsTo
          Value = ArcIRJsonInternal.pointer path }

    /// Creates an artifact-qualified reference for a typed ArcIR JSON location.
    let fragmentRef artifact location =
        { Artifact = artifact
          Selector = selector location }

    /// Enumerates every selectable entity and value occurrence in a graph.
    let locations (ir: ArcIR) =
        seq {
            for KeyValue(termId, _) in ir.Terms do
                ArcJsonLocation.Term termId

            for KeyValue(objectId, object') in ir.Objects do
                ArcJsonLocation.Object objectId

                for KeyValue(assertionId, _) in object'.Types do
                    ArcJsonLocation.TypeAssertion(objectId, assertionId)

                for KeyValue(assertionId, property) in object'.Properties do
                    ArcJsonLocation.Property(objectId, assertionId)
                    ArcJsonLocation.PropertyValue(objectId, assertionId)

                    for KeyValue(annotationId, _) in property.Annotations do
                        ArcJsonLocation.PropertyAnnotation(objectId, assertionId, annotationId)
                        ArcJsonLocation.PropertyAnnotationValue(objectId, assertionId, annotationId)

                for KeyValue(annotationId, _) in object'.Annotations do
                    ArcJsonLocation.ObjectAnnotation(objectId, annotationId)
                    ArcJsonLocation.ObjectAnnotationValue(objectId, annotationId)

            for KeyValue(relationId, relation) in ir.Relations do
                ArcJsonLocation.Relation relationId

                for KeyValue(assertionId, property) in relation.Properties do
                    ArcJsonLocation.RelationProperty(relationId, assertionId)
                    ArcJsonLocation.RelationPropertyValue(relationId, assertionId)

                    for KeyValue(annotationId, _) in property.Annotations do
                        ArcJsonLocation.RelationPropertyAnnotation(relationId, assertionId, annotationId)
                        ArcJsonLocation.RelationPropertyAnnotationValue(relationId, assertionId, annotationId)

                for KeyValue(annotationId, _) in relation.Annotations do
                    ArcJsonLocation.RelationAnnotation(relationId, annotationId)
                    ArcJsonLocation.RelationAnnotationValue(relationId, annotationId)
        }

    /// Resolves an RFC 6901 selector against JSON in a caller-owned stream and returns a detached JSON element.
    let resolve selector stream =
        ArcIRJsonInternal.readJsonElement JsonPointerConformsTo selector stream

    /// Resolves a typed location against JSON in a caller-owned stream and returns a detached JSON element.
    let resolveLocation location stream = resolve (selector location) stream

    /// Verifies an artifact digest and resolves its selected JSON fragment as a detached JSON element.
    let resolveFragment (fragment: FragmentRef) =
        try
            let bytes = File.ReadAllBytes fragment.Artifact.Path

            if not (ArtifactRevision.verifyBytes fragment.Artifact bytes) then
                Error
                    [ ArcIRJsonInternal.error
                          "arcir.json.digest-mismatch"
                          (sprintf
                              "Artifact '%s' does not match SHA-256 digest %s."
                              fragment.Artifact.Path
                              fragment.Artifact.Sha256) ]
            else
                use stream = new MemoryStream(bytes, false)
                resolve fragment.Selector stream
        with
        | :? IOException as ex -> Error [ ArcIRJsonInternal.error "arcir.json.read-failed" ex.Message ]
        | :? UnauthorizedAccessException as ex -> Error [ ArcIRJsonInternal.error "arcir.json.read-failed" ex.Message ]
        | :? ArgumentException as ex -> Error [ ArcIRJsonInternal.error "arcir.json.read-failed" ex.Message ]
        | :? NotSupportedException as ex -> Error [ ArcIRJsonInternal.error "arcir.json.read-failed" ex.Message ]

    /// A reversible reader implementation for dependency-injected persistence boundaries.
    let Reader =
        { new IArcIRReader with
            member _.Read stream = read stream }

    /// A deterministic writer implementation for dependency-injected persistence boundaries.
    let Writer =
        { new IArcIRWriter with
            member _.Write(stream, ir) = write stream ir }
