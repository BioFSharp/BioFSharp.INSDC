namespace BioFSharp.INSDC.Crawler

open System

/// Pure builders for the two ENA REST endpoints the crawler uses: the Portal
/// API `filereport` (discovery — enumerate everything connected to a root) and
/// the Browser API XML endpoint (fetch the record `*_SET` for a batch of
/// accessions). Kept side-effect free so the URL shapes are unit-testable.
module Endpoints =

    /// Default base URL of the ENA Portal API `filereport` search endpoint.
    [<Literal>]
    let DefaultPortalBaseUrl = "https://www.ebi.ac.uk/ena/portal/api/filereport"

    /// Default base URL of the ENA Browser API XML record endpoint.
    [<Literal>]
    let DefaultBrowserBaseUrl = "https://www.ebi.ac.uk/ena/browser/api/xml"

    /// The run-level fields requested from `filereport`. Each row couples a run
    /// with its experiment, sample, study (ENA's `study_accession`, i.e. the
    /// BioProject) and secondary study (ENA's `secondary_study_accession`, i.e.
    /// the SRA/ENA Study) — the full connected set plus the parent
    /// relationships in one response — and the run's FASTQ files (`fastq_ftp`
    /// with matching `fastq_md5` / `fastq_bytes`, semicolon-separated), which
    /// are not present in the run XML itself.
    [<Literal>]
    let FileReportFields =
        "run_accession,experiment_accession,sample_accession,study_accession,secondary_study_accession,fastq_ftp,fastq_md5,fastq_bytes"

    /// Builds the Portal API `filereport` URL that enumerates every run (and its
    /// connected experiment/sample/study) belonging to `rootAccession`, keyed
    /// off `portalBaseUrl`.
    let portalFileReport (portalBaseUrl: string) (rootAccession: string) : string =
        sprintf
            "%s?accession=%s&result=read_run&fields=%s&format=tsv"
            portalBaseUrl
            (Uri.EscapeDataString rootAccession)
            FileReportFields

    /// Builds the Browser API URL that returns the XML `*_SET` for one batch of
    /// comma-separated `accessions` (all expected to be the same entity kind),
    /// keyed off `browserBaseUrl`. Accessions are plain `[A-Z0-9]` tokens, so
    /// they are joined without escaping (escaping the commas would break the
    /// path segment).
    let browserXml (browserBaseUrl: string) (accessions: seq<string>) : string =
        sprintf "%s/%s" browserBaseUrl (String.concat "," accessions)
