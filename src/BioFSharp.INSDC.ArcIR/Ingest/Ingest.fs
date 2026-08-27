namespace BioFSharp.INSDC.ArcIR

open System.IO
open BioFSharp.ArcIR

/// The ingest facade: fold supplementary sources (a paper, a count-data archive) into ArcIR fragments and
/// incorporate them into an existing INSDC-derived graph. Fragments are `ConversionResult`s keyed by
/// accession, so cross-references dangle until the referenced record is present, exactly like the INSDC
/// converters. See plans/arcir-ingest.md.
[<RequireQualifiedAccess>]
module Ingest =

    /// Convert a paper (already-read metadata + file descriptor) into fragments: a `publication` Resource,
    /// its authors as person Agents, and pending `references` edges to the related dataset accessions.
    let paper (meta: PaperMetadata) (file: ResourceFile) (relatedAccessions: string list) : ConversionResult =
        Paper.convert meta file relatedAccessions

    /// Convert one already-read count file into fragments.
    let countData (countFile: CountFile) : ConversionResult = CountData.convert countFile

    /// Convert and account for a JATS paper from its exact immutable artifact bytes.
    let paperWithAccounting
        (artifact: ArtifactRevision)
        (fileName: string)
        (bytes: byte array)
        (relatedAccessions: string list)
        : AccountedConversion =
        IngestAccounting.paper artifact fileName bytes relatedAccessions

    /// Convert and account for a delimited count table from its exact immutable artifact bytes.
    let countDataWithAccounting
        (artifact: ArtifactRevision)
        (fileName: string)
        (bytes: byte array)
        : AccountedConversion =
        IngestAccounting.countData artifact fileName bytes

    /// Read a paper from a JATS XML file and convert it, linking it to the given dataset accession(s).
    let paperFromJats (path: string) (relatedAccessions: string list) : ConversionResult =
        Paper.convert (IngestReaders.readJats path) (IngestReaders.describeFile path) relatedAccessions

    /// Read a JATS paper, verify it against the declared artifact revision, convert it, and account for its front matter.
    let paperFromJatsWithAccounting
        (artifact: ArtifactRevision)
        (path: string)
        (relatedAccessions: string list)
        : AccountedConversion =
        paperWithAccounting artifact (Path.GetFileName path) (File.ReadAllBytes path) relatedAccessions

    /// Read a count table, verify it against the declared artifact revision, convert it, and account for its header cells.
    let countDataFromFileWithAccounting
        (artifact: ArtifactRevision)
        (path: string)
        : AccountedConversion =
        countDataWithAccounting artifact (Path.GetFileName path) (File.ReadAllBytes path)

    /// Read a count-data zip archive and convert each tabular entry into fragments.
    let countDataFromArchive (path: string) : ConversionResult list =
        IngestReaders.readCountArchive path |> CountData.convertMany

    /// Read count files directly under a folder and convert each into fragments.
    let countDataFromFolder (path: string) : ConversionResult list =
        IngestReaders.readCountFolder path |> CountData.convertMany

    /// Fold ingestion fragments into an existing graph: add the new objects, then resolve pending
    /// references against the union of existing and new objects (so a paper's `references` edge lands on
    /// the real INSDC node when present, and dangles otherwise), and add the direct edges.
    let incorporate (existing: ArcIR) (results: ConversionResult seq) : ArcIR =
        let results = List.ofSeq results
        let objects = results |> List.collect (fun r -> r.Objects)
        let directRelations = results |> List.collect (fun r -> r.Relations)
        let pending = results |> List.collect (fun r -> r.Pending)

        let additions = GraphBuilder.assemble objects directRelations
        let withObjects = GraphBuilder.mergeOrFail existing additions
        let resolved = Mapping.resolveRelations withObjects.Objects.Values pending

        GraphBuilder.mergeOrFail withObjects (GraphBuilder.assemble [] resolved)
