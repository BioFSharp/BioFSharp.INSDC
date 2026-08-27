namespace BioFSharp.INSDC.ArcIR

open System.IO
open System.Text
open System.Xml
open BioFSharp.ArcIR

open BioFSharp.INSDC.ArcIR.GraphText

/// Serializes an [ArcIR] property graph to GraphML (http://graphml.graphdrawing.org/xmlns), the standard
/// interchange format read by Gephi, yEd, and Cytoscape desktop. Nodes carry `label`/`kind`/`dtypes`
/// plus one data column per distinct property IRI and per distinct annotation term; edges carry the
/// `predicate` label. Relations pointing at ids absent from `Objects` get a placeholder node
/// (`kind=Missing`) so the edge stays valid. Pure — no dependency beyond `System.Xml`.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module GraphMl =

    [<Literal>]
    let private ns = "http://graphml.graphdrawing.org/xmlns"

    /// Reserved GraphML key ids/attr.names; a colliding property/annotation name is suffixed with '_'.
    let private reserved = set [ "label"; "kind"; "dtypes"; "predicate" ]

    let private safeName (name: string) =
        if reserved.Contains name then name + "_" else name

    /// The property + annotation data of a node, merged into one column-name -> rendered-value map
    /// (values sharing a column name are joined). Column names are `safeName`d.
    let private nodeData (ir: ArcIR) (o: ArcObject) =
        let add name value m =
            m
            |> Map.change name (function
                | Some existing -> Some(existing + "; " + value)
                | None -> Some value)

        let withProps =
            o.Properties.Values
            |> Seq.fold (fun m property -> add (safeName (localName property.Predicate.Value)) (renderValue property.Value) m) Map.empty

        o.Annotations.Values
        |> Seq.fold (fun m a -> add (safeName (annotationName ir a)) (renderAnnotationValue ir a.Value) m) withProps

    let private edgeData (r: ArcRelation) =
        r.Properties.Values
        |> Seq.map (fun property -> safeName (localName property.Predicate.Value), renderValue property.Value)
        |> Map.ofSeq

    let private writeGraph (writer: TextWriter) (ir: ArcIR) =
        // Pass 1: collect the key schema and any dangling endpoints.
        let nodeColumns =
            seq {
                for o in ir.Objects.Values do
                    for property in o.Properties.Values -> safeName (localName property.Predicate.Value)
                    for a in o.Annotations.Values -> safeName (annotationName ir a)
            }
            |> Seq.distinct
            |> Seq.sort
            |> Seq.toList

        let edgeColumns =
            seq {
                for r in ir.Relations.Values do
                    for property in r.Properties.Values -> safeName (localName property.Predicate.Value)
            }
            |> Seq.distinct
            |> Seq.sort
            |> Seq.toList

        let nodeKeyId = nodeColumns |> List.mapi (fun i n -> n, sprintf "nd%d" i) |> Map.ofList
        let edgeKeyId = edgeColumns |> List.mapi (fun i n -> n, sprintf "ed%d" i) |> Map.ofList

        let missing =
            ir.Relations.Values
            |> Seq.collect (fun r -> [ r.Subject; r.Object ])
            |> Seq.distinct
            |> Seq.filter (fun id -> not (ir.Objects.ContainsKey id))
            |> Seq.sort
            |> Seq.toList

        // Pass 2: write.
        let settings = XmlWriterSettings(Indent = true)
        use xml = XmlWriter.Create(writer, settings)

        let writeKey id forWhat attrName =
            xml.WriteStartElement("key", ns)
            xml.WriteAttributeString("id", id)
            xml.WriteAttributeString("for", forWhat)
            xml.WriteAttributeString("attr.name", attrName)
            xml.WriteAttributeString("attr.type", "string")
            xml.WriteEndElement()

        let writeData keyId (value: string) =
            xml.WriteStartElement("data", ns)
            xml.WriteAttributeString("key", keyId)
            xml.WriteString(value)
            xml.WriteEndElement()

        xml.WriteStartDocument()
        xml.WriteStartElement("graphml", ns)

        writeKey "label" "node" "label"
        writeKey "kind" "node" "kind"
        writeKey "dtypes" "node" "dtypes"
        for name in nodeColumns do
            writeKey nodeKeyId.[name] "node" name
        writeKey "predicate" "edge" "predicate"
        for name in edgeColumns do
            writeKey edgeKeyId.[name] "edge" name

        xml.WriteStartElement("graph", ns)
        xml.WriteAttributeString("id", "G")
        xml.WriteAttributeString("edgedefault", "directed")

        for o in ir.Objects.Values do
            xml.WriteStartElement("node", ns)
            xml.WriteAttributeString("id", o.Id.Value)
            writeData "label" (nodeLabel ir o)
            writeData "kind" (kindName o.Kind)
            let dtypes = o.Types.Values |> Seq.map (fun assertion -> localName assertion.Term.Value) |> Seq.sort |> String.concat " "
            if dtypes <> "" then
                writeData "dtypes" dtypes
            for KeyValue(name, value) in nodeData ir o do
                writeData nodeKeyId.[name] value
            xml.WriteEndElement()

        // Placeholder nodes for dangling edge endpoints.
        for id in missing do
            xml.WriteStartElement("node", ns)
            xml.WriteAttributeString("id", id.Value)
            writeData "label" id.Value
            writeData "kind" "Missing"
            xml.WriteEndElement()

        ir.Relations.Values
        |> Seq.iteri (fun i r ->
            xml.WriteStartElement("edge", ns)
            xml.WriteAttributeString("id", r.Id.Value)
            xml.WriteAttributeString("source", r.Subject.Value)
            xml.WriteAttributeString("target", r.Object.Value)
            writeData "predicate" (localName r.Predicate.Value)
            for KeyValue(name, value) in edgeData r do
                writeData edgeKeyId.[name] value
            xml.WriteEndElement())

        xml.WriteEndElement() // graph
        xml.WriteEndElement() // graphml
        xml.WriteEndDocument()

    /// The GraphML document for `ir` as a string.
    let toString (ir: ArcIR) =
        use writer = new StringWriter()
        writeGraph writer ir
        writer.ToString()

    /// Write the GraphML document for `ir` to `path` (UTF-8, no BOM).
    let writeFile (path: string) (ir: ArcIR) =
        use writer = new StreamWriter(path, false, UTF8Encoding(false))
        writeGraph writer ir
