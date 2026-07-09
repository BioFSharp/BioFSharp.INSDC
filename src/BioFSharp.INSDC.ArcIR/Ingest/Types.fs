namespace BioFSharp.INSDC.ArcIR

open Arc.Build

/// File metadata for a resource we ingest (a paper file, a count-matrix file) — enough to build a
/// Resource node without reading the file's contents. Produced by the readers in `Ingest/Readers.fs`
/// and consumed by the ingest builders. See plans/arcir-ingest.md.
type ResourceFile =
    {
        /// The file name (no directory); used in the node id and as the `Filename` property.
        Name: string
        /// Size in bytes, if known.
        ByteSize: int64 option
        /// A content checksum, method-prefixed (e.g. `sha256:<hex>`), if computed.
        Checksum: string option
        /// The media/MIME type, if known (inferred from the extension by the readers).
        MediaType: string option
    }


[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ResourceFile =

    /// The file-metadata `Properties` bag for a Resource node built from this file (absent fields dropped).
    let properties (file: ResourceFile) : (Iri * ArcValue) list =
        [ Some(Iri.Create "Filename", ArcValue.String file.Name)
          file.ByteSize |> Option.map (fun b -> Iri.Create "ByteSize", ArcValue.Integer b)
          file.Checksum |> Option.map (fun c -> Iri.Create "Checksum", ArcValue.String c)
          file.MediaType |> Option.map (fun m -> Iri.Create "MediaType", ArcValue.String m) ]
        |> List.choose id


/// An author extracted from a paper. Modelled as a person Agent, deduped by email (else ORCID, else name),
/// so an author and an INSDC contact with the same key collapse to one enriched node.
type PaperAuthor =
    {
        Name: string option
        Email: string option
        Affiliation: string option
        Orcid: string option
    }


/// Paper-level metadata extracted from a JATS XML article (or supplied for a PDF).
type PaperMetadata =
    {
        Title: string option
        Doi: string option
        Journal: string option
        Authors: PaperAuthor list
    }


/// One data column of a count matrix, addressed by its 1-based position (RFC 7111 `#col=<Index>`) and
/// carrying the run accession that labels it in the header.
type CountColumn =
    {
        /// 1-based column position in the file (RFC 7111). The feature/gene-id column is 1.
        Index: int
        /// The run accession that heads this column.
        RunAccession: string
    }


/// A count-matrix file plus the run-accession columns parsed from its header line.
type CountFile =
    {
        File: ResourceFile
        Columns: CountColumn list
    }
