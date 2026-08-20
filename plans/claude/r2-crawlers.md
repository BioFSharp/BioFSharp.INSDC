# R2 crawlers — fetch the raw artifacts (INSDC XML, paper, DEE2 counts)

> **Status: DONE (2026-08-20).** The original acceptance criteria are implemented. This document is retained as an as-built historical record; future evolution is governed by [the active implementation plan](../implementation.md).

> The ArcIR export plan (`plans/claude/arcir-export-readiness.md`) defines R2 as "the full INSDC XML
> record tree in a folder layout + counts (+ paper)". R2 needs no IR at all — it just needs the three
> raw source artifacts on disk in a canonical tree. This plan covers the **source-side** crawlers that
> fetch them. The export-side writer that consumes this tree is a separate (later) plan.
>
> **Revision 2026-07-12 (as-built).** This `plans/claude/` copy tracks the implemented crawlers. Two
> changes from the original `plans/glm-5.2/r2-crawlers.md` draft:
> 1. **DEE2 lookup** now resolves a study accession through the `search2.sh` accession-search endpoint
>    (one lookup per accession) instead of scraping the whole `huge/<species>/` directory listing, and
>    it keys on the study **accession**, not the submitter alias — see the DEE2 sections below.
> 2. **Paper discovery** gained an auto-discovery path (`paperId = None`) from the crawled records' own
>    publication xrefs, in addition to the direct-id path.

## Context

The repo can already read/write INSDC records and ingest JATS papers + count files into the ArcIR.
But every record it has seen is a hand-committed fixture. To materialize R2 for a real project, we need
to hit the upstream sources and land their raw outputs on disk in the shapes `IngestReaders` already
consumes:

- **INSDC** — the ENA Browser API XML records (BioProject, Study, BioSample, Experiment, Run), one
  file per accession in a project-shaped tree.
- **Paper** — EuropePMC full text, JATS XML preferred (metadata extractable by `IngestReaders.readJats`),
  with a PDF fallback when no open-access full text XML exists.
- **DEE2** — the per-project RNA-seq count bundle (a zip of `GeneCountMatrix.tsv`, etc.), resolved for a
  study accession (SRP/ERP/DRP) via the DEE2 `search2.sh` accession-search endpoint and downloaded from
  the `.zip` link it returns (typically `dee2.io/huge/<species>/<SRP>[_GSE].zip`).

The existing `BioFSharp.INSDC.Crawler` project already crawls ENA and persists typed records into
SQLite. This plan **reuses that project** (same net8.0, same FsHttp, same `CrawlOptions`/`Discovery`
machinery) and adds two new modules + one orchestrator that write raw files to disk instead of (or
in addition to) SQLite.

## Settled decisions (from clarifying session 2026-07-11, DEE2 revised 2026-07-12)

| Decision | Resolution |
|---|---|
| **Home** | One project (`BioFSharp.INSDC.Crawler`); new modules `Paper.fs`, `Dee2.fs` alongside the existing `Crawler.fs`. No new projects. The INSDC XML writer folds into `Crawler.fs` (reusing the `BioFSharp.IO.INSDC` writers) — no separate `XmlSave` module. |
| **INSDC XML layout** | Project-shaped tree (see below) — not the flat `<entity>/<acc>.xml` of the first draft. |
| **INSDC XML content** | Round-tripped through the `BioFSharp.IO.INSDC` readers + writers (`readString` → `write`). Reuses the existing `Fetch.crawlCore` pipeline (which already parses into typed records) and the same `write` functions the roundtrip tests exercise, so the output is guaranteed to re-parse. |
| **Paper discovery** | Two paths: a caller-supplied `paperId` (a **PMCID**) fetches that paper directly; `paperId = None` auto-discovers from the crawled records' own publication xrefs (`<XREF_LINK>` `PUBMED`/`PMC` on the BioProject/Study), resolving a PMID to a PMCID via EuropePMC. |
| **Paper format** | Try JATS XML first; on 4xx/empty/non-XML, fall back to PDF. `PaperResult` DU captures which landed. |
| **DEE2 resolution** | Resolve the study accession via `search2.sh?org=<species>&accessionsearch=<accession>`, then download the `.zip` href on the result page (or `None` when the page reads "No results found"). Replaces the earlier `huge/<species>/` full-listing scrape — one lookup per accession, no bulk index download. |
| **DEE2 lookup key** | The SRA study **accession** (SRP/ERP/DRP) — the archive-assigned `Study.Accession`, **not** the submitter `Alias`. A GEO-origin study's alias is its GEO series id (e.g. `GSE125950`), which DEE2 never keys on; the accession (e.g. `SRP183179`) is what `search2.sh` resolves. |
| **DEE2 species** | Caller-supplied `species` parameter (e.g. `"athaliana"`). No taxon-id → species mapping. |
| **Orchestrator** | `crawlAll` runs all three in sequence against one project accession, writing under one `<outDir>`. |
| **Binary fetch** | New `FetchBytes : string -> Async<byte[]>` seam on `CrawlOptions` (defaulting to `Internal.Http.getBytes`). The text `Fetch` stays text-only — widening it to bytes would corrupt every existing test. |

## On-disk layout

### `crawlToXml` — INSDC record tree

```
<outDir>/
  <BioProject-accession>.xml        # e.g. PRJDB5192.xml — at root, one file per discovered BioProject
  <Study-accession>.xml             # e.g. DRP003416.xml — at root, one file per discovered Study (absent if none)
  samples/
    <BioSample-accession>.xml
  experiments/
    <Experiment-accession>.xml
  runs/
    <Run-accession>.xml
```

- BioProject and Study live at the root (not under a per-entity folder) — they are the project-level
  containers; samples/experiments/runs are their children.
- Idempotent resume: skip any file that already exists (mirrors the SQLite insert dedup). The log
  reports `WritingXml(written, skipped, kind)` per entity.
- Directories are created on demand (`Directory.CreateDirectory` is a no-op if the dir exists).

### `crawlAll` — the unified tree

```
<outDir>/
  <BioProject-accession>.xml
  <Study-accession>.xml
  samples/        *.xml
  experiments/    *.xml
  runs/           *.xml
  paper/          <sanitized-id>.jats.xml | <sanitized-id>.pdf    # whichever succeeded; absent if skipped/failed
  counts/         <accession>.zip                                 # the DEE2 bundle, named by the study accession (the SRP)
```

`crawlAll` writes everything under one `<outDir>`; `crawlToXml`, `Paper.crawlPaper`, and `Dee2.crawlDee2`
are the building blocks (and remain independently callable).

## Public API surface

### `Endpoints.fs` (additions — pure URL builders)

```fsharp
// EuropePMC full-text + search endpoints
val europePmcFullTextXml : baseUrl:string -> id:string -> string   // "<base>/<id>/fullTextXML"
val europePmcFullTextPdf  : baseUrl:string -> id:string -> string   // "<base>/<id>/fullTextPDF"
val europePmcSearch       : baseUrl:string -> query:string -> pageSize:int -> string  // "<base>/search?query=…"
DefaultEuropePmcBaseUrl : string = "https://www.ebi.ac.uk/europepmc/webservices/rest"

// DEE2 accession-search endpoint
val dee2Search : baseUrl:string -> species:string -> accession:string -> string
                 // "<base>?org=<species>&accessionsearch=<accession>"
DefaultDee2SearchBaseUrl : string = "http://dee2.io/cgi-bin/search2.sh"
```

### `CrawlOptions` (additions)

```fsharp
/// The HTTP GET used to fetch a URL's body as bytes. Injectable so tests can run without network
/// access; defaults to `Internal.Http.getBytes`. Used only by PDF/zip (binary) fetches.
val FetchBytes : string -> Async<byte[]>

/// Base URL of the DEE2 `search2.sh` accession-search CGI (bundle lookup).
val Dee2SearchBaseUrl : string
```

- `Default` sets `FetchBytes = Internal.Http.getBytes` and `Dee2SearchBaseUrl = Endpoints.DefaultDee2SearchBaseUrl`.
- Existing text-only `Fetch` stays unchanged — no existing test breaks.

### `Crawler.fs` — INSDC XML writer (additions to the existing module)

```fsharp
/// Write every discovered INSDC record for `accession` as per-accession XML
/// files under <outDir>, in the project-shaped tree (BioProject/Study at root;
/// samples/experiments/runs in subfolders). Records are round-tripped through
/// the BioFSharp.IO.INSDC readers + writers (the same path the roundtrip tests
/// exercise). Idempotent: existing files are skipped.
val crawlToXmlWithAsync : CrawlOptions -> accession:string -> outDir:string -> Async<unit>
val crawlToXmlAsync     : accession:string -> outDir:string -> Async<unit>
val crawlToXml          : accession:string -> outDir:string -> unit   // blocking
```

Implementation reuses `Fetch.crawlCore` (discover + fetch + parse into typed records) then writes each
record via the IO `write` function (`BioProject.write`, `Study.write`, `BioSample.write`,
`Experiment.write`, `Run.write`). The per-kind folder mapping (`folderFor`) and the skip-if-exists loop
(`writeRecords`) are private helpers in `Crawler.fs` — no separate raw-XML splitting module.

### `Paper.fs` (new module)

```fsharp
/// The result of a paper crawl — which format landed on disk, or none.
type PaperResult =
    | JatsXml  of path: string
    | Pdf      of path: string
    | NotFound

/// Fetch the full text of paper `id` (a PMCID) from EuropePMC. Tries JATS XML first; if that fails
/// (4xx/empty/non-XML), falls back to PDF. Writes to <outDir>/paper/<sanitized-id>.<ext>.
/// `JatsXml` is the only format `IngestReaders.readJats` can parse today — `Pdf` is a raw artifact
/// for the disk tree / future text extraction, not a feedstock for the current ingest pipeline.
val crawlPaperWithAsync : CrawlOptions -> id:string -> outDir:string -> Async<PaperResult>
val crawlPaperAsync     : id:string -> outDir:string -> Async<PaperResult>
val crawlPaper          : id:string -> outDir:string -> PaperResult   // blocking

/// Auto-discover a paper straight from an INSDC record's own links — no caller-supplied id. Extracts
/// the publication xrefs (`<XREF_LINK>` PUBMED/PMC, preferring a direct PMC ref), resolves the first to
/// a PMCID via EuropePMC `search`, then fetches its full text. `NotFound` when no xref resolves to a
/// PMCID with full text. This is the `paperId = None` path of `crawlAll`.
val discoverAndCrawlWithAsync : CrawlOptions -> links:seq<Link> -> outDir:string -> Async<PaperResult>
```

### `Dee2.fs` (new module)

```fsharp
/// Parse a DEE2 `search2.sh` result page into the bundle download URL. Returns None when the page reads
/// "No results found" or carries no `.zip` link; otherwise the absolute `.zip` URL. Pure (no I/O).
val parseSearchResult : html:string -> string option

/// Resolve the DEE2 bundle download URL for one SRA study `accession` via the `search2.sh` endpoint.
/// One HTTP GET; None when the accession has no bundle.
val resolveBundleUrlWithAsync : CrawlOptions -> species:string -> accession:string -> Async<string option>
val resolveBundleUrlAsync     : species:string -> accession:string -> Async<string option>
val resolveBundleUrl          : species:string -> accession:string -> string option   // blocking

/// Download the DEE2 project bundle for SRA study `accession` (resolved via search2.sh). Writes the zip
/// to <outDir>/counts/<accession>.zip (atomically, via a .tmp + move). Returns the written path, or None
/// if the accession has no DEE2 bundle (logged as `BundleNotFound`).
val crawlDee2WithAsync : CrawlOptions -> species:string -> accession:string -> outDir:string -> Async<string option>
val crawlDee2Async     : species:string -> accession:string -> outDir:string -> Async<string option>
val crawlDee2          : species:string -> accession:string -> outDir:string -> string option   // blocking
```

### `Crawler.fs` — orchestrator (addition)

```fsharp
/// Per-folder file counts from `crawlAll`, so the caller can verify what landed.
type CrawlSummary =
    { ProjectDir  : string                   // <outDir>
      InsdcCounts : Map<string, int>        // "root","samples","experiments","runs"
      Paper       : PaperResult              // JatsXml/Pdf/NotFound
      Dee2Path    : string option }          // <outDir>/counts/<accession>.zip, or None

/// Crawl INSDC XML + paper + DEE2 counts for one project accession, writing under one <outDir>.
/// `paperId = Some pmcid` fetches that paper directly; `None` auto-discovers from the crawled records'
/// publication xrefs. `dee2Species = None` skips the DEE2 step. The DEE2 step keys on the first
/// discovered Study's `Accession` (the SRP); no study → skip.
val crawlAllWithAsync : CrawlOptions -> accession:string -> outDir:string ->
                          paperId:string option -> dee2Species:string option -> Async<CrawlSummary>
val crawlAllAsync      : accession:string -> outDir:string ->
                          paperId:string option -> dee2Species:string option -> Async<CrawlSummary>
val crawlAll           : accession:string -> outDir:string ->
                          paperId:string option -> dee2Species:string option -> CrawlSummary   // blocking
```

Implementation order inside `crawlAllWithAsync`:
1. INSDC discovery + write (`crawlToXmlCore` — `Fetch.crawlCore` for the typed records + `DiscoveredSet`,
   then per-accession XML via the IO writers). The typed records also carry the Study accession + the
   publication links reused below, so there is no second Portal API call.
2. Paper: `paperId = Some id` → `Paper.crawlPaperWithAsync options id outDir`; `None` →
   `Paper.discoverAndCrawlWithAsync options links outDir`, where `links` are the BioProject/Study
   `ProjectLinks`/`StudyLinks` already in hand.
3. DEE2 (if `dee2Species = Some s` and at least one Study was fetched): `Dee2.crawlDee2WithAsync options
   s study.Accession outDir`, using the first discovered Study's **accession** (the SRP) as the
   `search2.sh` key. No study → log `BundleNotFound(s, "<none>")`, `None`.
4. Fold into `CrawlSummary`.

## Logging additions (`Log.fs`)

New `CrawlEvent` cases alongside the existing ones:

```fsharp
| WritingXml         of written:int * skipped:int * kind:string
| FetchedPaperFormat of id:string * format:string * path:string   // format = "jats" | "pdf"
| FetchPaperFailed   of id:string * error:string
| DiscoveredPaperRef of refLabel:string * pmcid:string option     // auto-discovery: which xref, resolved PMCID
| FetchedBundle      of accession:string * path:string
| BundleNotFound     of species:string * accession:string
```

`Log.format` updated for each. `Log.console`/`Log.file` unchanged — they already render through `format`.

## Wiring

### fsproj (`BioFSharp.INSDC.Crawler.fsproj`)

Add to the existing `<Compile Include=…>` list, in order (so dependencies precede dependents):

```xml
<Compile Include="Endpoints.fs" />
<Compile Include="Log.fs" />
<Compile Include="Internal\Http.fs" />
<Compile Include="CrawlOptions.fs" />
<Compile Include="Discovery.fs" />
<Compile Include="Paper.fs" />          <!-- NEW -->
<Compile Include="Dee2.fs" />            <!-- NEW -->
<Compile Include="Crawler.fs" />
```

`Paper`/`Dee2` before `Crawler` so the `crawlAll` orchestrator can reference them.

### Dependencies

No new ProjectReferences or PackageReferences. The new code uses:
- FsHttp (already referenced) for the binary `getBytes` default.
- `System.Text.Json` for the EuropePMC search response; `System.Text.RegularExpressions` for the DEE2
  `search2.sh` href scrape; `System.IO` / `System.Net.Http` — all in-box on net8.0.
- `Internal.Http.withRetry` (existing) for retry-with-backoff around the new fetches.
- `BioFSharp.IO.INSDC` writers (`BioProject.write`, `Study.write`, `BioSample.write`, `Experiment.write`, `Run.write`) — `crawlToXml` round-trips the typed records through these.

### AGENTS.md updates

Under "Crawler (dev / inspection tier)", add a new subsection:

> ### R2 raw-artifact crawlers (same project)
>
> Three additional surfaces write raw source artifacts to disk for R2 (the structured-standard
> AI readiness level format), in a project-shaped tree. All reuse `CrawlOptions`/`Internal.Http` and live in the
> same `BioFSharp.INSDC.Crawler` project:
>
> - `Crawler.crawlToXml : accession -> outDir -> unit` — INSDC XML record tree.
> - `Paper.crawlPaper : id -> outDir -> PaperResult` — EuropePMC full text (JATS XML, PDF fallback);
>   `Paper.discoverAndCrawl` auto-discovers the paper from a record's PUBMED/PMC xrefs.
> - `Dee2.crawlDee2 : species -> accession -> outDir -> string option` — DEE2 project bundle zip,
>   resolved by study accession through `search2.sh`.
> - `Crawler.crawlAll : accession -> outDir -> paperId option -> dee2Species option -> CrawlSummary`
>   — orchestrates all three under one `<outDir>`.
>
> New endpoints: EuropePMC `fullTextXML`/`fullTextPDF`/`search`; DEE2 `search2.sh` accession lookup.

## Testing (xUnit, matching the existing offline-crawler idiom)

Stub `CrawlOptions.Fetch` (and `FetchBytes` for the PDF/zip cases) to return committed fixtures per URL,
exactly as the existing `CrawlerTests` do. No network at test time; one live variant gated on
`INSDC_LIVE_TESTS=1`.

> **Stub ordering gotcha:** the DEE2 search URL carries the study accession too
> (`…&accessionsearch=DRP003416`), so a URL-substring stub must match `"search2.sh"` **before** the
> accession-keyed INSDC-XML branches, or the search fetch returns the wrong fixture.

### New fixtures under `tests/fixtures/`

- `paper-PRJDB5192.pdf` — tiny hand-crafted PDF matching `paper-PRJDB5192.jats.xml`'s title/doi (so
  the fallback test confirms `PaperResult.Pdf` carries the right path without depending on real PDF
  parsing). Minimal valid PDF: `%PDF-1.4` header + one page object + `%%EOF`.
- `dee2-search-DRP003416.html` — a hand-trimmed DEE2 `search2.sh` result page carrying one unquoted
  `href=http://dee2.io/huge/athaliana/DRP003416.zip` link (the "No results found" case is asserted with
  an inline string literal, no fixture needed).
- `dee2-DRP003416.zip` — a tiny zip with one `GeneCountMatrix.tsv` so `IngestReaders.readCountArchive`
  can parse it (mirrors the existing `counts-PRJDB5192.zip` regeneration recipe in the fixtures README).
  Regenerate after editing the TSV:
  ```bash
  cd tests/fixtures && python3 -c "import zipfile; zipfile.ZipFile('dee2-DRP003416.zip','w',zipfile.ZIP_DEFLATED).write('dee2-DRP003416.tsv', arcname='GeneCountMatrix.tsv')"
  ```
- The INSDC XML fixtures (`PRJDB5192.xml`, `DRP003416.xml`, `SAMD00064197.xml`, `DRX066772.xml`,
  `DRR072834.xml`) already exist and are reused as the Browser API batch responses.
  `crawl-PRJDB5192.filereport.tsv` already exists for discovery.
- `paper-PRJDB5192.jats.xml` already exists as the JATS response fixture.

### Tests (modules in `Tests.fs`)

1. **`EndpointsTests`** — `europePmcFullTextXml`/`Pdf`/`search` and `dee2Search` build the exact URLs
   (string equality).
2. **`CrawlerXmlTests`**:
   - `crawlToXmlAsync` with stubbed `Fetch` writes the expected per-accession files under the
     project-shaped tree (assert `Directory.GetFiles` on root + each subfolder). Temp `outDir`, deleted in `finally`.
   - Re-run skips existing files (second run writes 0 new files → `Directory.GetFiles` counts unchanged).
3. **`PaperCrawlerTests`**:
   - `crawlPaper` on a JATS stub → `JatsXml path`, file exists, path ends with `<id>.jats.xml`.
   - `crawlPaper` on a 404 XML stub + a PDF stub (`FetchBytes`) → `Pdf path`, file exists, path ends with `.pdf`.
   - `crawlPaper` on both-stubbed-failing → `NotFound`, no `paper/` file.
   - `publicationRefs` extracts PUBMED/PMC xrefs (and ignores ENA housekeeping links); DOI id with `/`
     → sanitized to `_` in the filename.
4. **`Dee2CrawlerTests`**:
   - `dee2Search` builds the expected `search2.sh?org=…&accessionsearch=…` URL.
   - `parseSearchResult` extracts the `.zip` URL from a result page, and returns `None` on a
     "No results found" page.
   - `resolveBundleUrlWithAsync` returns the bundle URL via a stubbed fetch.
   - `crawlDee2Async` writes the bundle zip when the accession resolves → `Some path`.
   - `crawlDee2Async` on a no-results page → `None`, no `counts/` folder, `BundleNotFound` logged.
5. **`CrawlAllTests`**:
   - (a) all three succeed → JATS paper + DEE2 zip + XML tree, `CrawlSummary` fields populated.
   - (b) paper returns `Pdf` (XML stub 404, PDF stub bytes) → `Paper = Pdf _`, `paper/<id>.pdf` on disk.
   - (c) paper `NotFound` (both stubs fail) → `Paper = NotFound`, no `paper/` file.
   - (d) `paperId = None` with no PUBMED/PMC xref on the records → discovery finds nothing, `NotFound`.
   - (e) `dee2Species = None` → no DEE2 fetch attempted; `Dee2Path = None`.
6. **Live (opt-in `INSDC_LIVE_TESTS=1`)**: one real project with an open-access paper and a DEE2
   `athaliana` bundle.

## Verification

- `./build.sh` — builds the solution with zero `CS1591` (missing-XML-doc) warnings (every new public
  member carries `///` docs).
- `./build.sh runtests` — runs the new offline tests in the suite.
- Manual: `playground/crawl_all.fsx` calling `Crawler.crawlAll "<PRJ…>" "out" None (Some "athaliana")`,
  then inspect the tree and the console log.

## Future extensions (noted, not in this plan)

- **Per-run DEE2 fetch** via `cgi-bin/request.sh?org=<species>&x=<SRR>...` for projects with no bundled
  `search2.sh` hit (e.g. only partially processed). Needs the per-species metadata TSV lookup.
- **PDF text extraction** — turn a `PaperResult.Pdf` into a JATS-equivalent metadata blob so the ingest
  pipeline can handle both formats. Left for post-R2 since R2 only needs the raw artifact on disk.
- **Whole-project DEE2** — when a project has multiple studies, iterate `crawlDee2` over each Study
  accession discovered (currently `crawlAll` keys on the first study only).
