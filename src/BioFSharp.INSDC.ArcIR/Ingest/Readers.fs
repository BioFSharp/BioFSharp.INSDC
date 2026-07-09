namespace BioFSharp.INSDC.ArcIR

open System
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text.RegularExpressions
open System.Xml.Linq

/// The disk-touching side of ingestion: read the source formats into the pure descriptors the builders
/// consume. Paper metadata comes from JATS XML (in-box `System.Xml.Linq`); count files are read from a
/// loose folder or a zip archive (in-box `System.IO.Compression`), parsing only the header line. Kept
/// separate from the pure builders so those stay unit-testable offline. See plans/arcir-ingest.md.
[<RequireQualifiedAccess>]
module IngestReaders =

    let private mediaType (name: string) : string option =
        match Path.GetExtension(name).ToLowerInvariant() with
        | ".xml" -> Some "application/xml"
        | ".pdf" -> Some "application/pdf"
        | ".tsv" -> Some "text/tab-separated-values"
        | ".csv" -> Some "text/csv"
        | ".txt" -> Some "text/plain"
        | ".zip" -> Some "application/zip"
        | _ -> None

    let private isTabular (name: string) =
        match Path.GetExtension(name).ToLowerInvariant() with
        | ".tsv"
        | ".csv"
        | ".txt" -> true
        | _ -> false

    // A run accession: SRR/ERR/DRR followed by digits (the header cells that name a run column).
    let private runAccession = Regex(@"^[SED]RR\d+$", RegexOptions.Compiled)

    let private countResourceFile (name: string) (byteSize: int64) : ResourceFile =
        { Name = name; ByteSize = Some byteSize; Checksum = None; MediaType = mediaType name }

    // ---- files ----

    /// Describe a file as a `ResourceFile`: name, byte size, a `sha256:<hex>` checksum, and a media type
    /// inferred from the extension. Used for the paper file.
    let describeFile (path: string) : ResourceFile =
        let info = FileInfo path

        let checksum =
            use stream = File.OpenRead path
            use sha = SHA256.Create()
            "sha256:" + (BitConverter.ToString(sha.ComputeHash stream).Replace("-", "").ToLowerInvariant())

        { Name = info.Name; ByteSize = Some info.Length; Checksum = Some checksum; MediaType = mediaType info.Name }

    // ---- paper (JATS XML) ----

    let private attr (name: string) (e: XElement) =
        e.Attributes() |> Seq.tryPick (fun a -> if a.Name.LocalName = name then Some a.Value else None)

    let private attrEquals (name: string) (value: string) (e: XElement) =
        match attr name e with
        | Some v -> String.Equals(v, value, StringComparison.OrdinalIgnoreCase)
        | None -> false

    let private text (value: string) =
        match value with
        | null -> None
        | s when String.IsNullOrWhiteSpace s -> None
        | s -> Some(s.Trim())

    /// Read paper-level metadata from a JATS XML article: title, DOI, journal, and the authors
    /// (name, email, affiliation, ORCID). Elements are matched by local name, so namespaced and
    /// non-namespaced JATS both work.
    let readJats (path: string) : PaperMetadata =
        let root = XDocument.Load(path: string).Root
        let descendants (name: string) = root.Descendants() |> Seq.filter (fun e -> e.Name.LocalName = name)
        let firstText name = descendants name |> Seq.tryHead |> Option.bind (fun e -> text e.Value)

        let doi =
            descendants "article-id"
            |> Seq.tryPick (fun e -> if attrEquals "pub-id-type" "doi" e then text e.Value else None)

        let affById =
            descendants "aff"
            |> Seq.choose (fun e -> attr "id" e |> Option.bind (fun id -> text e.Value |> Option.map (fun v -> id, v)))
            |> Map.ofSeq

        let authors =
            descendants "contrib"
            |> Seq.filter (attrEquals "contrib-type" "author")
            |> Seq.map (fun c ->
                let child name = c.Descendants() |> Seq.filter (fun e -> e.Name.LocalName = name) |> Seq.tryHead
                let childText name = child name |> Option.bind (fun e -> text e.Value)

                let name =
                    match childText "given-names", childText "surname" with
                    | Some g, Some s -> Some(g + " " + s)
                    | Some g, None -> Some g
                    | None, Some s -> Some s
                    | None, None -> childText "string-name"

                let orcid =
                    c.Descendants()
                    |> Seq.filter (fun e -> e.Name.LocalName = "contrib-id")
                    |> Seq.tryPick (fun e -> if attrEquals "contrib-id-type" "orcid" e then text e.Value else None)

                let affiliation =
                    match childText "aff" with
                    | Some a -> Some a
                    | None ->
                        c.Descendants()
                        |> Seq.filter (fun e -> e.Name.LocalName = "xref" && attrEquals "ref-type" "aff" e)
                        |> Seq.tryPick (fun e -> attr "rid" e |> Option.bind affById.TryFind)

                { Name = name; Email = childText "email"; Affiliation = affiliation; Orcid = orcid })
            |> List.ofSeq

        { Title = firstText "article-title"; Doi = doi; Journal = firstText "journal-title"; Authors = authors }

    // ---- count data ----

    /// Parse a count-matrix header line into its run-accession columns, each with its 1-based position
    /// (RFC 7111). Splits on tab if present, else comma; keeps only cells matching a run accession.
    let parseHeader (headerLine: string) : CountColumn list =
        if String.IsNullOrWhiteSpace headerLine then
            []
        else
            let delimiter = if headerLine.Contains "\t" then '\t' else ','

            headerLine.Split delimiter
            |> Array.mapi (fun i cell -> i + 1, cell.Trim())
            |> Array.choose (fun (index, cell) -> if runAccession.IsMatch cell then Some { Index = index; RunAccession = cell } else None)
            |> List.ofArray

    /// Read one loose count file: its metadata plus the run-accession columns parsed from its header.
    let readCountFile (path: string) : CountFile =
        let info = FileInfo path
        let header = File.ReadLines path |> Seq.tryHead |> Option.defaultValue ""
        { File = countResourceFile info.Name info.Length; Columns = parseHeader header }

    /// Read every tabular (`.tsv`/`.csv`/`.txt`) file directly under a folder as a count file.
    let readCountFolder (path: string) : CountFile list =
        Directory.EnumerateFiles path
        |> Seq.filter (Path.GetFileName >> isTabular)
        |> Seq.map readCountFile
        |> List.ofSeq

    /// Read every tabular entry of a zip archive as a count file, parsing only each entry's header line.
    let readCountArchive (path: string) : CountFile list =
        use stream = File.OpenRead path
        use archive = new ZipArchive(stream, ZipArchiveMode.Read)

        archive.Entries
        |> Seq.filter (fun e -> not (String.IsNullOrEmpty e.Name) && isTabular e.Name)
        |> Seq.map (fun entry ->
            let header =
                use entryStream = entry.Open()
                use reader = new StreamReader(entryStream)

                match reader.ReadLine() with
                | null -> ""
                | line -> line

            { File = countResourceFile entry.Name entry.Length; Columns = parseHeader header })
        |> List.ofSeq
