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

    /// Default base URL of the EuropePMC REST article endpoint.
    [<Literal>]
    let DefaultEuropePmcBaseUrl = "https://www.ebi.ac.uk/europepmc/webservices/rest"

    /// Builds the EuropePMC full-text XML (JATS) URL for one article `id`,
    /// keyed off `europePmcBaseUrl`. `id` may be a PMC id (`PMCXXXXX`), a DOI
    /// (`10.1xxx/yyy`), or a PMID (`PMID:nnn`); EuropePMC's `fullTextXML`
    /// endpoint accepts these formats directly. DOIs carry `/`, which are not
    /// escaped here — the caller passes the raw id and EuropePMC resolves it.
    let europePmcFullTextXml (europePmcBaseUrl: string) (id: string) : string =
        sprintf "%s/%s/fullTextXML" europePmcBaseUrl id

    /// Default base URL of the PMC Open Access dataset on AWS — the source of
    /// article **PDFs**. Public bucket (`pmc-oa-opendata`, us-east-1), no
    /// authentication.
    ///
    /// EuropePMC has no working PDF endpoint: the `fullTextPDF` path this crawler
    /// originally used **404s for every article**, including ones whose
    /// `fullTextXML` serves fine and which EuropePMC itself flags open access. Its
    /// advertised browser route (`europepmc.org/articles/<id>?pdf=render`) 404s too.
    /// NCBI's OA service does hand out PDF links, but they point into the legacy
    /// FTP tree that NCBI moved under `deprecated/` and **deletes in August 2026**,
    /// so building on it would break within weeks. The AWS Open Access dataset is
    /// the sanctioned, durable replacement.
    [<Literal>]
    let DefaultPmcOaBaseUrl = "https://pmc-oa-opendata.s3.amazonaws.com"

    /// Builds the PMC Open Access URL for one article's PDF, keyed off
    /// `pmcOaBaseUrl`. Articles are stored per *version* under a
    /// `PMC<id>.<version>/` prefix, so the key repeats the versioned id:
    ///
    ///     `<base>/PMC7430643.1/PMC7430643.1.pdf`
    ///
    /// `pmcid` must be the canonical `PMCXXXXX` form; `version` is normally `1`
    /// (see `Paper.PmcOaVersions` for the ladder tried when it is not).
    let pmcOaPdf (pmcOaBaseUrl: string) (pmcid: string) (version: int) : string =
        sprintf "%s/%s.%d/%s.%d.pdf" pmcOaBaseUrl pmcid version pmcid version

    /// Builds the EuropePMC REST `search` URL for `query`, capped at `pageSize`
    /// hits and returning the JSON `LITE` result, keyed off `europePmcBaseUrl`.
    /// The whole `query` is URL-escaped — its field operators survive as
    /// `%3A`/`%20` and EuropePMC decodes them. Used to resolve a bare PubMed id
    /// to its PMCID (the full-text sources are keyed on PMCID only) via
    /// `query = "EXT_ID:<pmid> AND SRC:MED"`, or a PMCID to its article metadata
    /// via `query = "PMCID:<pmcid>"`.
    let europePmcSearch (europePmcBaseUrl: string) (query: string) (pageSize: int) : string =
        sprintf
            "%s/search?query=%s&format=json&resultType=lite&pageSize=%d"
            europePmcBaseUrl
            (Uri.EscapeDataString query)
            pageSize

    /// Default base URL of the DEE2 `search2.sh` accession-search CGI. HTTP
    /// (not HTTPS) — the DEE2 server serves this over plain HTTP.
    [<Literal>]
    let DefaultDee2SearchBaseUrl = "http://dee2.io/cgi-bin/search2.sh"

    /// Builds the DEE2 `search2.sh` URL that resolves one SRA study `accession`
    /// (SRP/ERP/DRP) to its project bundle for `species`, keyed off
    /// `dee2SearchBaseUrl`. The result page carries an `href=…zip` link to the
    /// bundle (or the text "No results found"). Preferred over scraping the full
    /// `huge/<species>/` directory listing: one lookup per accession instead of
    /// downloading and parsing the entire (very large) per-species index.
    let dee2Search (dee2SearchBaseUrl: string) (species: string) (accession: string) : string =
        sprintf
            "%s?org=%s&accessionsearch=%s"
            dee2SearchBaseUrl
            (Uri.EscapeDataString species)
            (Uri.EscapeDataString accession)
