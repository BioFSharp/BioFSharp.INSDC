# BioFSharp.INSDC.Crawler implementation plan

> **Status: DONE (2026-08-20).** The original acceptance criteria are implemented. This document is retained as historical design context; future evolution is governed by [the active implementation plan](../implementation.md).

## Context

The repo can already read/write and model INSDC records, but every record it has
seen is a hand-committed fixture. To move toward inspecting **real-world** records we
need a way to start from a single project accession, discover every record connected
to it (study, samples, experiments, runs), download their XML from ENA, and land the
parsed records in SQLite for inspection.

This adds a new project `BioFSharp.INSDC.Crawler` exposing two entry points:

- `crawlToSqlite : projectAccession -> sqlitePath -> ()` — crawl + persist.
- `crawl : projectAccession -> CrawlResult` — crawl + return the typed records.

Settled decisions:
- **Persistence reuses the existing normalized store** `BioFSharp.INSDC.SQLite`
  (not a raw-XML blob table). `BioProject.listAccessions` there is already documented
  as "suitable for crawler resume".
- **net8.0** (FsHttp 15.x is net6.0+, so the `netstandard2.0` convention in AGENTS.md
  cannot apply here). It is still packed and published to NuGet like every other
  `src/` project.
- **Return a named record including `Studies`** (a project may have no BioProject
  record, so Study must be surfaced independently). A mirrored study-first entry point
  is a noted future extension.
- **Tests are offline** against committed fixtures, plus one opt-in live smoke test.

## Discovery + fetch strategy (verified against the live ENA APIs)

1. **Discover** — one call to the ENA Portal API resolves the whole connected set:
   `https://www.ebi.ac.uk/ena/portal/api/filereport?accession=<ROOT>&result=read_run&fields=run_accession,experiment_accession,sample_accession,study_accession,secondary_study_accession&format=tsv`
   Verified with `PRJDB5192`: every row carries `study_accession=PRJDB5192` (the
   BioProject), `secondary_study_accession=DRP003416` (the DDBJ Study), plus the
   sample/experiment/run. Column → entity kind (no accession-prefix guessing). Each
   row also yields the **relationships** the FK-threaded inserts need
   (study→project, experiment→study, run→experiment).
2. **Fetch (batched)** — the Browser API accepts comma-separated accessions and
   returns a `*_SET`: `https://www.ebi.ac.uk/ena/browser/api/xml/<acc>,<acc>,...`
   Verified: `.../xml/DRR072834,DRR072835` → `<RUN_SET>` with 2 runs. One request per
   entity kind (chunked, ≤~100 accessions/request) instead of hundreds — ~5 requests
   per project.
3. **Parse** — feed each batched response to the existing set-aware readers
   (`BioProject.readString`, `Study.readString`, `BioSample.readString`,
   `Experiment.readString`, `Run.readString`), each `string -> seq<Entity>`.
4. **Assemble/persist** — collect into `CrawlResult`; for `crawlToSqlite`, insert via
   `BioFSharp.INSDC.SQLite` in FK order (below).

## Public API surface (`Crawler.fs`)

```fsharp
namespace BioFSharp.INSDC.Crawler
open BioFSharp.FileFormats.INSDC

/// The connected INSDC records discovered for a root accession.
type CrawlResult =
    { BioProjects : BioProject[]   // may be empty (study with no umbrella project)
      Studies     : Study[]
      BioSamples  : BioSample[]
      Experiments : Experiment[]
      Runs        : Run[] }

[<RequireQualifiedAccess>]
module Crawler =
    val crawlAsync        : ?options:CrawlOptions -> accession:string -> Async<CrawlResult>
    val crawl             : accession:string -> CrawlResult                       // blocking wrapper
    val crawlToSqliteAsync: ?options:CrawlOptions -> accession:string -> sqlitePath:string -> Async<unit>
    val crawlToSqlite     : accession:string -> sqlitePath:string -> unit         // blocking wrapper
```

`crawl`/`crawlToSqlite` are thin `Async.RunSynchronously` wrappers so the exact
`accession -> sqlitePath -> ()` and `accession -> records` shapes exist, while the
async cores stay composable.

## Project layout (`src/BioFSharp.INSDC.Crawler/`)

Files listed explicitly in the fsproj (F# has no globbing). **Every public member
gets `///` XML docs** (AGENTS.md rule, enforced by `GenerateDocumentationFile=true`).

- `Entity.fs` — `type Entity = BioProject | Study | BioSample | Experiment | Run`.
- `Endpoints.fs` — pure URL builders: `portalFileReport`, `browserXml`. Base URLs from
  `CrawlOptions` so tests/mirrors override. Pure ⇒ unit-testable.
- `Log.fs` — `CrawlEvent` DU + `Log.console` (default, built-in) + `Log.silent`.
- `CrawlOptions.fs` — `{ BaseBrowserUrl; BasePortalUrl; MaxConcurrency; Retries;
  ThrottleMs; ChunkSize; Fetch; Log }` + `Default`. `Fetch : string -> Async<string>`
  is the injectable seam (real FsHttp default; stubbed in tests).
- `Internal/Http.fs` — FsHttp GET wrapped with retry + backoff + throttle.
- `Discovery.fs` — `parse : tsv -> DiscoveredSet` (pure) + `discoverAsync`.
- `Crawler.fs` — batched fetch + parse + assemble; the public surface; SQLite persist.

### `DiscoveredSet` carries relationships for FK threading

```fsharp
type DiscoveredSet =
    { BioProjects : string list
      Studies     : string list
      BioSamples  : string list
      Experiments : string list
      Runs        : string list
      StudyToProject    : Map<string,string>   // study  -> project (study_accession)
      ExperimentToStudy : Map<string,string>   // exp    -> study  (secondary_study_accession)
      RunToExperiment   : Map<string,string> } // run    -> experiment
```

## Persistence details (reusing `BioFSharp.INSDC.SQLite`)

The store's inserts thread parent accessions and enforce FKs, so **order and parent
wiring matter**:

- `BioSample.insert conn sample` — takes no parent accession. INSDC does **not** own a
  sample under a single parent (one sample feeds many experiments across studies), so
  the `biosample` table has no owner FK column. A sample is nonetheless always tied to
  its project — **transitively, via the experiments that use it**: each
  `experiment_sample_descriptor.accession` is a soft FK back to `biosample`. That link
  is written by `Experiment.insert`, so **samples must be inserted before experiments**
  for the soft FK to resolve under `foreign_keys=ON`.
- `BioProject.insert conn project` — top of the ownership chain.
- `Study.insert conn bioProjectAccession study` — `bioProjectAccession` nullable; pass
  the project accession **only if that project row was inserted**, else `null`.
- `Experiment.insert conn studyAccession experiment` — study_accession **NOT NULL**;
  the Study row must already exist. Also writes the sample-descriptor soft FK, so its
  referenced BioSample must already exist too.
- `Run.insert conn experimentAccession run` — experiment_accession **NOT NULL**; the
  Experiment row must already exist.

Insert order (ownership chain + the sample soft FK): **BioProject → Study → BioSample →
Experiment → Run**, threading parents from the `DiscoveredSet` maps. (BioSample only
needs to precede Experiment; placing it after Study keeps the read as the natural
project→study→sample→experiment→run hierarchy.)

- Open the connection with `PRAGMA foreign_keys = ON` (matches the store's own
  `Internal.Sql.openConnection`); FK-on turns any ordering mistake into a hard failure
  instead of silent orphans.
- `ensureSchema` — query `sqlite_master` for a known table (`bioproject`); if absent,
  call `Schema.init connection` (it throws on an already-initialized DB).
- **Idempotent resume** — read each `<Entity>.listAccessions` into a set and insert
  only new accessions; a re-run resumes instead of hitting PK collisions.
- **Skip-and-log** an experiment/run whose required parent wasn't inserted (e.g. study
  fetch failed) rather than aborting the whole crawl.

## ENA connectivity table (`accession_relations`) — added to the store

Beyond the record tables, the crawl persists the ENA `read_run` connectivity graph
into a new `accession_relations` table in `BioFSharp.INSDC.SQLite`
(`AccessionRelations` module + `AccessionRelation` type). One row per run links
`run / experiment / sample / study / project` (+ `root_accession`, `fetched_at`),
with **no foreign keys**, so connectivity is recorded even for records whose XML
failed to download. It makes the sample→project link a one-hop query (otherwise only
reachable transitively via `experiment_sample_descriptor`).

Two gotchas the crawl had to handle (both now captured in code comments + AGENTS.md):
- **FK re-enable:** `insdc_schema.sql` contains `PRAGMA foreign_keys = ON`, so
  `Schema.init` turns enforcement back on. The crawl disables it again *after* init.
- **Dual sample accession:** an experiment's `SAMPLE_DESCRIPTOR` references the SRA
  sample accession (`DRS...`) while the sample row is keyed by its BioSample
  accession (`SAMD...`, what the filereport returns) — the same sample under two
  accessions. This makes the `experiment_sample_descriptor` soft FK legitimately
  dangle, which is why the crawl runs with `foreign_keys = OFF`.

## Built-in logging

Dependency-free, testable event model (avoids pulling in `Microsoft.Extensions.Logging`):

```fsharp
type CrawlEvent =
    | Discovered of counts: Map<string,int>
    | Fetching of kind: string * count: int
    | Parsed of kind: string * count: int
    | Retrying of url: string * attempt: int * error: string
    | Persisted of kind: string * inserted: int * skipped: int
    | Failed of context: string * error: string
    | Completed of summary: string
```

`options.Log : CrawlEvent -> unit`; default `Log.console` prints readable progress
out of the box; users can forward events to their own logger/`ILogger`.

## Robustness / improvements folded in

- **Injectable `Fetch`** — makes the crawler fully offline-testable.
- **Batched + chunked fetch** — ~5 requests/project (verified `*_SET` support).
- **Bounded-concurrency + throttle** — `Async.Parallel` capped by `MaxConcurrency`
  with a per-request delay, to stay polite to ENA.
- **Retry with exponential backoff** on transient failures (429/503/timeouts).
- **Deduplication** of discovered accessions before fetching.
- **Partial-failure tolerance** — a bad accession is logged (`Failed`) and the crawl
  continues; the summary reports counts.
- **Graceful no-BioProject case** — `BioProjects` may be empty; `Studies` still populated.
- **Future (noted): mirrored `crawlFromStudy`** — filereport accepts a study accession.

## Wiring

- Add the fsproj to the `/src/` folder of `BioFSharp.INSDC.slnx` (explicit list).
- fsproj: `net8.0`, `GenerateDocumentationFile=true`, plus the standard NuGet package
  metadata block (Authors/Description/tags/icon/README) so it packs like the other
  `src/` projects. ProjectRefs: `BioFSharp.FileFormats.INSDC`, `BioFSharp.IO.INSDC`,
  `BioFSharp.INSDC.SQLite`. PackageRefs: `FsHttp` 15.0.3, `Microsoft.Data.Sqlite` 8.0.10.
- Test project: add ProjectRefs to `BioFSharp.INSDC.Crawler` **and**
  `BioFSharp.INSDC.SQLite` (currently references neither). No `build/ProjectInfo.fs`
  change — the single test project is already in `testProjects`.
- Update **AGENTS.md**: crawler in the layout tree; the net8.0 target as the
  explicit exception to "shipped projects are netstandard2.0"; the ENA Portal
  (discovery) + Browser (fetch) endpoints; the offline-tests rule.

## Testing (xUnit, matching repo idiom)

Add a `CrawlerTests` module inside the existing `tests/.../Tests.fs` to reuse the
`TestFiles`, `ObjectGraph`, `Assertions` helpers (round-trips compare the object
graph via `ObjectGraph.equal`, never raw XML).

New committed fixtures under `tests/fixtures/`:
- `crawl-PRJDB5192.filereport.tsv` — a trimmed discovery response containing the one
  connected quintet (`DRR072834 / DRX066772 / SAMD00064197 / PRJDB5192 / DRP003416`).
- Batched XML responses reuse the **existing** `*_SET`-rooted fixtures (`PRJDB5192.xml`,
  `DRP003416.xml`, `SAMD00064197.xml`, `DRX066772.xml`, `DRR072834.xml`).

Tests:
1. `Discovery.parse` → expected deduplicated `DiscoveredSet` (incl. relationship maps).
2. `Endpoints` build the exact portal + browser URLs (incl. comma-joined batch).
3. **Round-trip into types** — `crawlAsync` with a stubbed `Fetch` returns a
   `CrawlResult` whose each array is `ObjectGraph.equal` to the directly-parsed fixture.
4. **Round-trip into SQLite** — `crawlToSqliteAsync` to a temp DB, reopen, `<Entity>.tryGet`
   each accession back, `ObjectGraph.equal` against the crawled record. Temp DB deleted
   in a `finally`.
5. **Resume/idempotency** — running twice inserts once, skips the second time.
6. **Opt-in live smoke** — gated on `INSDC_LIVE_TESTS=1`; really crawls a small public
   project. Off in CI. (Pick a genuinely small project accession.)

## Verification

- `./build.sh` — builds the solution incl. the new project.
- `./build.sh runtests` — runs the offline crawler round-trips with the suite.
- Manual: a `playground/crawl.fsx` calling
  `Crawler.crawlToSqlite "PRJDB5192" "prjdb5192.sqlite"`, then inspect the DB tables and
  the built-in console log (discovery → fetch → persist).
- `INSDC_LIVE_TESTS=1 dotnet test` once, to exercise the real ENA path end-to-end.
