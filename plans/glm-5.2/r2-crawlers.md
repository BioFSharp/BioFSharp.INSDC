# R2 crawlers — fetch the raw artifacts (INSDC XML, paper, DEE2 counts)

> **Status: SUPERSEDED (2026-08-20).** This draft was replaced directly by the [as-built R2 crawler plan](../claude/r2-crawlers.md) and is no longer authoritative. Implemented portions remain historical context; current replacement work is consolidated in [the active implementation plan](../implementation.md).

> The ArcIR export plan (`plans/glm-5.2/arcir-export-readiness.md`) defines R2 as "the full INSDC XML
> record tree in a folder layout + counts (+ paper)". R2 needs no IR at all — it just needs the three
> raw source artifacts on disk in a canonical tree. This plan covers the **source-side** crawlers that
> fetch them. The export-side writer that consumes this tree is a separate (later) plan.

## Context

The repo can already read/write INSDC records and ingest JATS papers + count files into the ArcIR.
But every record it has seen is a hand-committed fixture. To materialize R2 for a real project, we need
to hit the upstream sources and land their raw outputs on disk in the shapes `IngestReaders` already
consumes:

- **INSDC** — the ENA Browser API XML records (BioProject, Study, BioSample, Experiment, Run), one
  file per accession in a project-shaped tree.
- **Paper** — EuropePMC full text, JATS XML preferred (metadata extractable by `IngestReaders.readJats`),
  with a PDF fallback when no open-access full text XML exists.
- **DEE2** — the per-project RNA-seq count bundle (a zip of `GeneCountMatrix.tsv`, etc.), downloaded
  from `dee2.io/huge/<species>/<SRP>[_GSE].zip`.

The existing `BioFSharp.INSDC.Crawler` project already crawls ENA and persists typed records into
SQLite. This plan **reuses that project** (same net8.0, same FsHttp, same `CrawlOptions`/`Discovery`
machinery) and adds three new modules + one orchestrator that write raw files to disk instead of (or
in addition to) SQLite.

## Settled decisions (from clarifying session 2026-07-11)

| Decision | Resolution |
|---|---|
| **Home** | One project (`BioFSharp.INSDC.Crawler`); new modules `Paper.fs`, `Dee2.fs`, `XmlSave.fs` alongside the existing `Crawler.fs`. No new projects. |
| **INSDC XML layout** | Project-shaped tree (see below) — not the flat `<entity>/<acc>.xml` of the first draft. |
| **INSDC XML content** | Round-tripped through the `BioFSharp.IO.INSDC` readers + writers (`readString` → `write`). Reuses the existing `Fetch.crawlCore` pipeline (which already parses into typed records) and the same `write` functions the roundtrip tests exercise, so the output is guaranteed to re-parse. |
| **Paper discovery** | Direct DOI/PMC/PMID input (caller-supplied `paperId`). No accession→paper auto-discovery; the caller resolves the linkage. |
| **Paper format** | Try JATS XML first; on 4xx/empty/non-XML, fall back to PDF. `PaperResult` DU captures which landed. |
| **DEE2 download path** | Project bundle off `huge/<species>/` (one zip per SRP). Per-run `cgi-bin/request.sh` is a noted future extension. |
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
  counts/         <SRP>.zip                                  # the DEE2 bundle, renamed from the raw filename
```

`crawlAll` writes everything under one `<outDir>`; `crawlToXml`, `Paper.crawlPaper`, and `Dee2.crawlDee2`
are the building blocks (and remain independently callable).

## Public API surface

### `Endpoints.fs` (additions — pure URL builders)

```fsharp
// EuropePMC full-text endpoints
val europePmcFullTextXml : baseUrl:string -> id:string -> string   // "<base>/<id>/fullTextXML"
val europePmcFullTextPdf  : baseUrl:string -> id:string -> string   // "<base>/<id>/fullTextPDF"
DefaultEuropePmcBaseUrl : string = "https://www.ebi.ac.uk/europepmc/webservices/rest"

// DEE2 huge-bundle endpoints
val dee2HugeBundleList : baseUrl:string -> species:string -> string     // "<base>/<species>/"
val dee2HugeBundle     : baseUrl:string -> species:string -> filename:string -> string
DefaultDee2HugeBaseUrl : string = "http://dee2.io/huge"
```

### `CrawlOptions` (addition)

```fsharp
/// The HTTP GET used to fetch a URL's body as bytes. Injectable so tests can run without network
/// access; defaults to `Internal.Http.getBytes`. Used only by PDF (binary) fetches.
val FetchBytes : string -> Async<byte[]>
```

- `Default` sets `FetchBytes = Internal.Http.getBytes`.
- Existing text-only `Fetch` stays unchanged — no existing test breaks.

### `Crawler.fs` (additions to the existing module)

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

Implementation reuses `Fetch.crawlCore` (discover + fetch + parse into typed
records) then writes each record via the IO `write` function (`BioProject.write`,
`Study.write`, `BioSample.write`, `Experiment.write`, `Run.write`). No separate
raw-XML splitting step — the typed records are already available from the
existing pipeline, and the roundtrip tests prove the writers preserve the data.

### `Crawler.fs` (additions to the existing module)

```fsharp
/// Write every discovered INSDC record for `accession` as per-accession XML files under <outDir>,
/// in the project-shaped tree (BioProject/Study at root; samples/experiments/runs in subfolders).
/// Idempotent: existing files are skipped.
val crawlToXmlWithAsync : CrawlOptions -> accession:string -> outDir:string -> Async<unit>
val crawlToXmlAsync     : accession:string -> outDir:string -> Async<unit>
val crawlToXml          : accession:string -> outDir:string -> unit   // blocking
```

### `Paper.fs` (new module)

```fsharp
/// The result of a paper crawl — which format landed on disk, or none.
type PaperResult =
    | JatsXml  of path: string
    | Pdf      of path: string
    | NotFound

/// Fetch the full text of a paper from EuropePMC. Tries JATS XML first; if that fails (4xx/empty),
/// falls back to PDF. Writes to <outDir>/paper/<sanitized-id>.<ext> (id is DOI/PMC/PMID; `/` → `_`).
/// `JatsXml` is the only format `IngestReaders.readJats` can parse today — `Pdf` is a raw artifact
/// for the disk tree / future text extraction, not a feedstock for the current ingest pipeline.
val crawlPaperWithAsync : CrawlOptions -> id:string -> outDir:string -> Async<PaperResult>
val crawlPaperAsync     : id:string -> outDir:string -> Async<PaperResult>
val crawlPaper          : id:string -> outDir:string -> PaperResult   // blocking
```

### `Dee2.fs` (new module)

```fsharp
/// List the bundles available at `huge/<species>/` as an SRP-accession → filename map.
/// Scrapes the HTML directory listing (filenames match `SRP\\d+[_GSE\\d+]?\\.zip`).
val listBundlesWithAsync : CrawlOptions -> species:string -> Async<Map<string, string>>
val listBundlesAsync     : species:string -> Async<Map<string, string>>
val listBundles          : species:string -> Map<string, string>   // blocking

/// Download the DEE2 project bundle for `<srp>` from `huge/<species>/`. Writes the zip to
/// <outDir>/counts/<srp>.zip. Returns the written path, or None if no bundle matched.
val crawlDee2WithAsync : CrawlOptions -> species:string -> srp:string -> outDir:string -> Async<string option>
val crawlDee2Async     : species:string -> srp:string -> outDir:string -> Async<string option>
val crawlDee2          : species:string -> srp:string -> outDir:string -> string option   // blocking
```

### `Crawler.fs` — orchestrator (addition)

```fsharp
/// Per-folder file counts from `crawlAll`, so the caller can verify what landed.
type CrawlSummary =
    { ProjectDir  : string                   // <outDir>
      InsdcCounts : Map<string, int>        // "root","samples","experiments","runs"
      Paper       : PaperResult              // JatsXml/Pdf/NotFound
      Dee2Path    : string option }          // <outDir>/counts/<SRP>.zip, or None

/// Crawl INSDC XML + paper + DEE2 counts for one project accession, writing under one <outDir>.
/// `paperId`/`dee2Species` are options: omit either to skip that step (logged, no fetch attempted).
/// The DEE2 step picks the first Study accession from INSDC discovery as its SRP; no study → skip.
val crawlAllWithAsync : CrawlOptions -> accession:string -> outDir:string ->
                          paperId:string option -> dee2Species:string option -> Async<CrawlSummary>
val crawlAllAsync      : accession:string -> outDir:string ->
                          paperId:string option -> dee2Species:string option -> Async<CrawlSummary>
val crawlAll           : accession:string -> outDir:string ->
                          paperId:string option -> dee2Species:string option -> CrawlSummary   // blocking
```

Implementation order inside `crawlAllWithAsync`:
1. INSDC discovery (`Fetch.crawlCore` for the `DiscoveredSet`, keeping the parent maps) — reuse the
   existing discovery pipeline, but write per-accession XML via `XmlSave` instead of persisting to
   SQLite. The discovery set also yields the Study accessions for the DEE2 step.
2. Paper (if `paperId = Some id`): `Paper.crawlPaperWithAsync options id outDir`.
3. DEE2 (if `dee2Species = Some s` and at least one Study accession was discovered): `Dee2.crawlDee2WithAsync
   options s (firstStudy |> defaultArg "") outDir`. No study → log `BundleNotFound(s, "<none>")`, `None`.
4. Fold into `CrawlSummary`.

## Logging additions (`Log.fs`)

New `CrawlEvent` cases alongside the existing ones:

```fsharp
| WritingXml        of written:int * skipped:int * kind:string
| FetchedPaperFormat of id:string * format:string * path:string   // format = "jats" | "pdf"
| FetchPaperFailed  of id:string * error:string
| FetchedBundle     of srp:string * path:string
| BundleNotFound    of species:string * srp:string
```

`Log.format` updated for each. `Log.console`/`Log.file` unchanged — they already render through `format`.

`FetchedPaper` (from the earlier draft) is replaced by `FetchedPaperFormat` so the log distinguishes
JATS vs PDF without inspecting the path extension.

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
- `System.Xml` / `System.Net.Http` / `System.IO` / `System.IO.Compression` / `System.Text.RegularExpressions`
  — all in-box on net8.0.
- `Internal.Http.withRetry` (existing) for retry-with-backoff around the new fetches.
- `BioFSharp.IO.INSDC` writers (`BioProject.write`, `Study.write`, `BioSample.write`, `Experiment.write`, `Run.write`) — `crawlToXml` round-trips the typed records through these, reusing the same `readString` + `write` path the roundtrip tests exercise. No separate raw-XML splitting module.

### AGENTS.md updates

Under "Crawler (dev / inspection tier)", add a new subsection:

> ### R2 raw-artifact crawlers (same project)
>
> Three additional surfaces write raw source artifacts to disk for R2 (the structured-standard
> AI readiness level format), in a project-shaped tree. All reuse `CrawlOptions`/`Internal.Http` and live in the
> same `BioFSharp.INSDC.Crawler` project:
>
> - `Crawler.crawlToXml : accession -> outDir -> unit` — INSDC XML record tree.
> - `Paper.crawlPaper : id -> outDir -> PaperResult` — EuropePMC full text (JATS XML, PDF fallback).
> - `Dee2.crawlDee2 : species -> srp -> outDir -> string option` — DEE2 project bundle zip.
> - `Crawler.crawlAll : accession -> outDir -> paperId option -> dee2Species option -> CrawlSummary`
>   — orchestrates all three under one `<outDir>`.
>
> New endpoints: EuropePMC `fullTextXML`/`fullTextPDF`; DEE2 `huge/<species>/` listing + bundle.

## Testing (xUnit, matching the existing offline-crawler idiom)

Stub `CrawlOptions.Fetch` (and `FetchBytes` for the PDF case) to return committed fixtures per URL,
exactly as the existing `CrawlerTests` do. No network at test time; one live variant gated on
`INSDC_LIVE_TESTS=1`.

### New fixtures under `tests/fixtures/`

- `paper-PRJDB5192.pdf` — tiny hand-crafted PDF matching `paper-PRJDB5192.jats.xml`'s title/doi (so
  the fallback test confirms `PaperResult.Pdf` carries the right path without depending on real PDF
  parsing). Minimal valid PDF: `%PDF-1.4` header + one page object + `%%EOF`.
- `dee2-athaliana-huge-listing.html` — hand-trimmed HTML directory listing (`<a href="…">` entries)
  containing `DRP003416.zip` (fictional bundle for the fixture project) and a couple of decoy entries
  with different SRP accessions to exercise the scrape.
- `dee2-DRP003416.zip` — a tiny zip with one `GeneCountMatrix.tsv` so `IngestReaders.readCountArchive`
  can parse it (mirrors the existing `counts-PRJDB5192.zip` regeneration recipe in the fixtures README).
  Regenerate after editing the TSV:
  ```bash
  cd tests/fixtures && python3 -c "import zipfile; zipfile.ZipFile('dee2-DRP003416.zip','w',zipfile.ZIP_DEFLATED).write('dee2-DRP003416.tsv', arcname='GeneCountMatrix.tsv')"
  ```
- The INSDC XML fixtures (`PRJDB5192.xml`, `DRP003416.xml`, `SAMD00064197.xml`, `DRX066772.xml`,
  `DRR072834.xml`) already exist and are reused as the Browser API batch responses. `crawl-PRJDB5192.filereport.tsv`
  already exists for discovery.
- `paper-PRJDB5192.jats.xml` already exists as the JATS response fixture.

### Tests (new modules in `Tests.fs`)

1. **`EndpointsTests`** (extend or add) — `europePmcFullTextXml`/`Pdf` and `dee2HugeBundle(List)` build
   the exact URLs (string equality).
2. **`XmlSaveTests`**:
   - `split` on a `RUN_SET` with 2 runs → 2 `(accession, outerXml)` pairs, accessions match the XML.
   - `split` on empty/invalid XML → `[]`.
   - `folderFor` maps each kind to the expected folder name (root for BioProject/Study; subfolder for others).
3. **`CrawlerXmlTests`**:
   - `crawlToXmlAsync` with stubbed `Fetch` writes the expected per-accession files under the
     project-shaped tree (assert `Directory.GetFiles` on root + each subfolder). Temp `outDir`, deleted in `finally`.
   - Re-run skips existing files (second run writes 0 new files → `Directory.GetFiles` counts unchanged).
4. **`PaperCrawlerTests`**:
   - `crawlPaper` on a JATS stub → `JatsXml path`, file exists, path ends with `<id>.jats.xml`.
   - `crawlPaper` on a 404 XML stub + a PDF stub (`FetchBytes`) → `Pdf path`, file exists, path ends with `.pdf`.
   - `crawlPaper` on both-stubbed-failing → `NotFound`, no `paper/` file.
   - DOI id with `/` → sanitized to `_` in the filename.
5. **`Dee2CrawlerTests`**:
   - `listBundlesWithAsync` parses the stubbed HTML listing into an `SRP -> filename` map (decoys + target).
   - `crawlDee2Async` writes the bundle zip when present → `Some path`.
   - `crawlDee2Async` missing bundle → `None`, no file written, `BundleNotFound` logged.
6. **`CrawlAllTests`**:
   - (a) all three succeed → JATS paper + DEE2 zip + XML tree, `CrawlSummary` fields populated.
   - (b) paper returns `Pdf` (XML stub 404, PDF stub bytes) → `Paper = Pdf _`, `paper/<id>.pdf` on disk.
   - (c) paper `NotFound` (both stubs fail) → `Paper = NotFound`, no `paper/` file.
   - (d) `paperId = None` → no paper fetch attempted (the paper fetch URL is never passed to `Fetch`).
   - (e) `dee2Species = None` → no DEE2 fetch attempted; `Dee2Path = None`.
   - (f) DEE2 bundle missing → `Dee2Path = None`; XML + paper still populated.
   - (g) no study discovered (project with no runs/studies) → DEE2 step skipped with
     `BundleNotFound(species, "<none>")`.
7. **Live (opt-in `INSDC_LIVE_TESTS=1`)**: one real EuropePMC id (e.g. a known open-access paper), one
   real DEE2 `athaliana` bundle for a small project.

## Verification

- `./build.sh` — builds the solution with zero `CS1591` (missing-XML-doc) warnings (every new public
  member carries `///` docs).
- `./build.sh runtests` — runs the new offline tests in the suite.
- Manual: a `playground/crawlAll.fsx` calling `Crawler.crawlAll "PRJDB5192" "out" (Some "10.1xxx/yyy")
  (Some "athaliana")`, then inspect the tree and the console log.

## Future extensions (noted, not in this plan)

- **Per-run DEE2 fetch** via `cgi-bin/request.sh?org=<species>&x=<SRR>...` for projects whose bundle
  isn't in `huge/` (e.g. partially processed). Needs the per-species metadata TSV lookup.
- **Paper discovery from accession** via EuropePMC's text-mining `annotationsByEntity` API (returns
  papers that *mention* a given accession) — broader coverage than the direct-id path, but limited to
  the PMC-open-access subset.
- **PDF text extraction** — turn a `PaperResult.Pdf` into a JATS-equivalent metadata blob so the ingest
  pipeline can handle both formats. Left for post-R2 since R2 only needs the raw artifact on disk.
- **Whole-project DEE2** — when a project has multiple studies, iterate `crawlDee2` over each SRP
  discovered (currently `crawlAll` picks the first study only).
