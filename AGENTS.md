# AGENTS.md

Operational guide for AI coding agents (and humans) working in `BioFSharp.INSDC`. Keep it short; read this before doing anything beyond a single-file edit.

## Project purpose

`BioFSharp.INSDC` provides read/write support for INSDC (International Nucleotide Sequence Database Collaboration) XML records — BioProject, Study, Sample, Experiment, Run, Analysis, Submission, Receipt — as direct dependencies of `BioFSharp`. The repo ships two packages:

- **`BioFSharp.FileFormats.INSDC`** — a **C#** library whose types are auto-generated from the ENA SRA v1.5 XSDs via [`dotnet-xscgen`](https://www.nuget.org/packages/dotnet-xscgen). C#, not F#, because there is no F# equivalent of `XmlSchemaClassGenerator`.
- **`BioFSharp.IO.INSDC`** — an F# wrapper around that type model exposing idiomatic `read` / `readString` / `write` / `writeString` per entity.

Both target `netstandard2.0` to match BioFSharp.

## Layout

```text
.
├── build/                                  FAKE build project (BuildSolution, RunTests, Pack, regenerateInsdcTypes, ...)
├── docs/                                   Placeholder only — no fsdocs site is published from this repo.
├── plans/implementation.md                 Authoritative implementation plan. Read this first.
├── src/
│   ├── BioFSharp.FileFormats.INSDC/        C# generated type model (.csproj)
│   │   ├── schemas/                          Committed ENA XSDs (sra_1_5/*.xsd)
│   │   └── Generated/                        Tool output — DO NOT HAND-EDIT
│   ├── BioFSharp.IO.INSDC/                 F# wrapper (.fsproj), one module per INSDC entity
│   ├── BioFSharp.INSDC.ArcIR/              F# mapping to an ARC intermediate representation (netstandard2.0)
│   ├── BioFSharp.INSDC.SQLite/             F# normalized SQLite store (netstandard2.0)
│   └── BioFSharp.INSDC.Crawler/            F# ENA crawler — net8.0 dev tool, NOT packaged (see below)
├── tests/
│   └── BioFSharp.INSDC.Tests/              xunit, one module per IO module
│       └── fixtures/<entity>/<acc>.xml     Committed real ENA records used by tests
├── .config/dotnet-tools.json               Pins `dotnet-xscgen` locally — `dotnet tool restore` after clone
├── BioFSharp.INSDC.slnx                    Solution file
├── build.cmd / build.sh                    Entry points to the FAKE build project
└── global.json                             SDK pin
```

## Build / test / pack commands

**Default to running FAKE build targets** rather than raw `dotnet` whenever the work touches more than one project — the build script is the source of truth for solution-wide configuration, test-coverage collection, and packaging. Use raw `dotnet` only when iterating on a single project in isolation.

| Task | Windows | macOS / Linux |
|------|---------|---------------|
| Build solution | `build.cmd` | `./build.sh` |
| Run tests | `build.cmd runtests` | `./build.sh runtests` |
| Pack nupkgs | `build.cmd pack` | `./build.sh pack` |
| Regenerate C# types from XSDs (only when schemas change) | `build.cmd regenerateInsdcTypes` | `./build.sh regenerateInsdcTypes` |

First-time setup after cloning:

```bash
dotnet tool restore     # installs the pinned dotnet-xscgen
```

## Conventions

- **F# IO modules** expose exactly `read` / `readString` / `write` / `writeString`. Do not invent variants. There is no `readLines` — INSDC files are XML, not line-based.
- **Every public F# member** carries `///` XML doc comments. Builds run with `GenerateDocumentationFile=true`; missing docs surface as `CS1591`-equivalent warnings.
- **The C# type model is generated.** Never hand-edit `src/BioFSharp.FileFormats.INSDC/Generated/`. To change the model, edit the XSDs in `schemas/` (rare) or adjust the generator flags in the `regenerateInsdcTypes` target, then re-run it.
- **Adding a new INSDC entity** is a four-step recipe: (1) commit the XSD into `schemas/`, (2) run `regenerateInsdcTypes`, (3) add a parallel F# IO module in `BioFSharp.IO.INSDC`, (4) add a parallel test module + fixture.

## Crawler (dev / inspection tier)

`src/BioFSharp.INSDC.Crawler` pulls real records from ENA so their output can be
inspected. It is the one project that does **not** target `netstandard2.0`: it
uses [FsHttp](https://www.nuget.org/packages/FsHttp) for HTTP, which needs
.NET 6+, so it is **net8.0**. It ships to NuGet like every other `src/` project.

- **Public surface** (`Crawler` module): `crawl : accession -> CrawlResult` and
  `crawlToSqlite : accession -> sqlitePath -> ()`, plus `*Async` and
  `*WithAsync` (take a `CrawlOptions`) variants. `CrawlResult` carries
  `BioProjects / Studies / BioSamples / Experiments / Runs`.
- **Discovery** hits the ENA Portal API `filereport` (`result=read_run`) to
  enumerate every run/experiment/sample/study connected to a project; **fetch**
  hits the ENA Browser API with comma-separated accessions (returns a `*_SET`,
  parsed by the existing IO `readString`). Both URLs are built in `Endpoints.fs`.
- **Persistence** reuses the `BioFSharp.INSDC.SQLite` store for records and adds
  the ENA connectivity graph to its `accession_relations` table. The crawl
  connection runs with `PRAGMA foreign_keys = OFF` (set after `Schema.init`,
  which enables it) because the store's soft references legitimately dangle — a
  sample is referenced by its SRA `DRS...` accession but stored under its
  BioSample `SAMD...` accession, the same sample under two accessions.
- **Tests are offline** by default: a stubbed `Fetch` maps ENA URLs to committed
  fixtures. The single live test is gated behind `INSDC_LIVE_TESTS=1`.

### Raw-artifact crawlers for the R1/R2 AI readiness level formats (same project)

R0–R4 are the **AI readiness level formats** — five on-disk representations of the
same project, each carrying more metadata and structure than the last (see
[`plans/claude/arcir-export-readiness.md`](plans/claude/arcir-export-readiness.md)).
**R1 and R2 need no ArcIR at all**: every file they emit is a raw artifact the
crawler downloads, so both are materialized here rather than by the export layer.
All the surfaces below reuse `CrawlOptions`/`Internal.Http` and live in the same
`BioFSharp.INSDC.Crawler` project (net8.0, FsHttp).

The crawler is **three layers, and the middle one is the point.** EuropePMC keys on
a PMCID and DEE2 keys on an SRA study accession; only the INSDC records know either.
Exposing that hop as ordinary functions is what lets you compose freely instead of
being limited to whatever combinations an orchestrator happens to bake in — so
**reach for the layers directly** and treat the orchestrators as conveniences.

**1. Resolve** — an INSDC accession → the ids the other services key on:

- `Crawler.resolve : accession -> InsdcRefs` — fetches only the BioProject and Study.
  Deliberately **not** the BioSample/Experiment/Run records: no paper or DEE2 lookup
  ever reads one, and on a big project they are thousands of wasted requests. Use
  `crawlAndDiscover` when you really do want the full record set.
- `Crawler.refsOf : accession -> CrawlResult -> DiscoveredSet -> InsdcRefs` — the same
  refs from records you already fetched, so the two projections below work whichever
  way you got them.
- `Crawler.paperRefs : InsdcRefs -> PublicationRef list` (pure) — the INSDC → EuropePMC
  hop. Feed to `Paper.resolvePmcid : PublicationRef list -> string option`.
- `Crawler.dee2Key : InsdcRefs -> string option` (pure) — the INSDC → DEE2 hop; the
  archive-assigned study `Accession`, never the submitter `Alias`.

The four things you'll actually want are then one-liners:

```fsharp
// paper, PMCID already in hand
Paper.crawlPaperFormat PaperFormat.Jats "PMC123456" outDir

// paper, discovered from an INSDC accession
let refs = Crawler.resolve "PRJNA123"
Crawler.paperRefs refs |> Paper.resolvePmcid
|> Option.map (fun id -> Paper.crawlPaperFormat PaperFormat.Jats id outDir)

// DEE2, study accession already in hand
Dee2.crawlDee2 "athaliana" "SRP183179" outDir

// DEE2, discovered from an INSDC accession
Crawler.resolve "PRJNA123" |> Crawler.dee2Key
|> Option.bind (fun srp -> Dee2.crawlDee2 "athaliana" srp outDir)
```

**2. Fetch** — one artifact each:

- `Crawler.crawlToXml : accession -> outDir -> ()` — INSDC XML record tree.
  BioProject + Study at the root (`<accession>.xml`); `samples/`,
  `experiments/`, `runs/` subfolders for the rest. Records are round-tripped
  through the `BioFSharp.IO.INSDC` readers + writers (the same path the
  roundtrip tests exercise). Idempotent: existing files are skipped (resume).
- `Paper.crawlPaper : id -> outDir -> PaperResult` — paper full text, taking whichever
  format is available: JATS XML first, falling back to PDF. `PaperResult` is
  `JatsXml | Pdf | NotFound`.
- **The two formats come from two different services.** JATS is EuropePMC
  (`fullTextXML`). The **PDF is the PMC Open Access dataset on AWS**
  (`pmc-oa-opendata`, public, no auth), keyed by *versioned* PMCID:
  `…/PMC7430643.1/PMC7430643.1.pdf`. EuropePMC has no usable PDF endpoint — its
  `fullTextPDF` path **404s for every article**, including ones whose JATS serves fine,
  so `PaperResult.Pdf` was unreachable in production until this was fixed. Every offline
  test passed anyway (the stub fabricates the bytes), so a `LIVE`-gated test now actually
  downloads a PDF and checks its `%PDF-` magic. Do not "restore" the EuropePMC PDF path.
  NCBI's OA service also hands out PDF links, but into an FTP tree it **deletes in
  August 2026** — don't build on that either.
- `Paper.crawlPaperFormat : PaperFormat -> id -> outDir -> PaperResult` — the
  same fetch pinned to **exactly one** format (`PaperFormat.Jats | PaperFormat.Pdf`),
  with **no fallback**. This is what R1 needs: an R1A that silently fell back to a
  PDF would in fact be an R1B.
- `id` in both is a **PMCID** — the EuropePMC full-text endpoints are keyed on the
  PMC id, so a DOI or bare PMID 404s. `Paper.discoverPmcid` resolves one from a
  record's own `PUBMED`/`PMC` `XREF_LINK`s (via EuropePMC `search`), and
  `Paper.discoverAndCrawl` pairs that discovery with the fetch.
- `Dee2.crawlDee2 : species -> accession -> outDir -> string option` — DEE2 project
  bundle zip, resolved by looking the SRA **study accession** (SRP/ERP/DRP) up
  through DEE2's `search2.sh` accession search and downloading the `.zip` it links.
  Keys on the archive-assigned `Accession`, **not** the submitter `Alias` (a
  GEO-origin study's alias is a GEO series id DEE2 never keys on). `species` is
  caller-supplied (e.g. `"athaliana"`). Writes `<outDir>/counts/<accession>.zip`.

**3. Compose** — thin conveniences over layers 1+2, one per readiness format. Both
take a `PaperSource` and a `Dee2Source` saying where each artifact comes from:

```fsharp
type PaperSource = PaperFrom of pmcid:string | PaperDiscover | PaperSkip
type Dee2Source  = Dee2From of species:string * studyAccession:string
                 | Dee2Discover of species:string | Dee2Skip
```

These are DUs and not `string option` on purpose: under the old signature `None`
meant *auto-discover* for the paper but *skip* for DEE2 — opposite senses in the
same call — and there was no way at all to say "fetch no paper".

- `Crawler.crawlAll : accession -> outDir -> PaperSource -> Dee2Source -> CrawlSummary`
  — **R2**: the INSDC XML tree + paper + DEE2 counts under one `<outDir>`. Its paper
  takes whichever full-text format is available.
- `Crawler.crawlR1Formats : R1Format list -> accession -> outDir -> PaperSource ->
  Dee2Source -> R1Summary list` — **R1** (`R1Format = R1A | R1B`): the DEE2 archive
  verbatim + the paper, pinned to JATS for `R1A` and PDF for `R1B`. Writes **no INSDC
  XML** — R1 obscures its source. Two properties are load-bearing, and a regression
  test locks each:
  - It resolves leanly (BioProject + Study only), never touching the
    BioSample/Experiment/Run records, so a large project costs a handful of requests
    instead of thousands.
  - R1A and R1B differ *only* in the paper, so passing both formats fetches discovery,
    the records, the PMCID and the DEE2 archive **exactly once** and only the paper per
    format — both renditions land in the one `<outDir>`. `Crawler.crawlR1` is
    single-format sugar; calling it twice re-crawls, so prefer `crawlR1Formats` for both.

Endpoints (EuropePMC `fullTextXML`/`search`; PMC OA `pmc-oa-opendata` PDFs; DEE2 `search2.sh`) are
pure URL builders in `Endpoints.fs`. `CrawlOptions` gains a
`FetchBytes : string -> Async<byte[]>` seam (defaults to `Internal.Http.getBytes`)
for binary fetches — the text `Fetch` stays text-only so existing tests are
unchanged.

`playground/crawl_all.fsx` and `playground/crawl_r1.fsx` drive these end-to-end,
committing each readiness format to its own orphan git branch (`R2B`, `R1A`, `R1B`)
in a per-accession curation repo. The DEE2 archive is a multi-MB binary, so both
scripts commit `counts/**` through **Git LFS** (provisioned by the
`ghcr.io/devcontainers/features/git-lfs:1` devcontainer feature). Two traps there,
and both fail *silently* — the commit succeeds and the zip goes in as a raw blob:
the `filter.lfs.*` config and the `.gitattributes` LFS rule must BOTH be in place
before `git add`, and since the orphan-branch wipe deletes `.gitattributes`, it has
to be re-written after every wipe. The scripts do this in their branch-init step and
print `git lfs ls-files` after each commit, which is the only place the mistake shows.

## Generated type naming (`typename-substitutions.txt`)

`dotnet xscgen` derives verbose C# type names from the XSD structure. We tame them with [`src/BioFSharp.FileFormats.INSDC/schemas/typename-substitutions.txt`](src/BioFSharp.FileFormats.INSDC/schemas/typename-substitutions.txt), passed to the tool via `--tnsf` in the [`regenerateInsdcTypes`](build/BasicTasks.fs) target. This is the single source of truth for friendly type names — never rename generated types by hand.

**File format.** One rule per line, `A:<xscgen-default-name>=<substitute>`. The `A:` prefix means "match any type or member" (xscgen accepts kind-specific prefixes too; we standardise on `A:`). Lines starting with `#` and blank lines are ignored. The header comment block lists the existing rename rules (A–F) the file applies — read it before adding rules so the codebase stays internally consistent.

**Adding or changing a rule:**

1. Edit `typename-substitutions.txt`. The left side is the name xscgen would produce *without any substitution*; the right side is the C# identifier you want. Both must be flat C# identifiers — dotted names like `Foo.Bar` emit invalid C# (`class Foo.Bar`).
2. Run `build.cmd regenerateInsdcTypes` (or `./build.sh regenerateInsdcTypes`).
3. Commit both the rule change and every regenerated file under `src/BioFSharp.FileFormats.INSDC/Generated/` so the substitution file matches the checked-in code.

**Removing a rule:** delete the line and regenerate. The type will revert to xscgen's verbose default — only do this when you also intend to rename it via a different rule.

**Pitfalls to avoid:**

- Substitution targets that collide with an *existing* xscgen-default name silently fall back to a generic suffix (`<Name>Item`). If a regenerated file appears with `Item` in its name, your substitute clashed with a sibling type's default — pick a longer-prefixed substitute.
- Rule keys must match xscgen's default name exactly. When in doubt, regenerate without `--tnsf` once locally to read off the defaults, then write rules against those.
- The substitution file is not regex-based; every rule is a literal type-name rename.

## CI is a thin shell around FAKE

The `.github/workflows/` files exist to set up a runner, restore the SDK, and **invoke a single FAKE target**. Any non-trivial logic — version parsing, gate checks, conditional skips, packaging, tagging — belongs in the build project under [`build/`](build/), not in the YAML.

Concretely:

- The release CI calls `./build.sh releaseFromNotes`; everything that flow does (parsing the topmost `### <version>` header from `RELEASE_NOTES.md`, the `(Unreleased)` skip, the "tag already exists" skip, clean/build/test/pack/push/tag) is implemented in [`build/ReleaseFromNotesTask.fs`](build/ReleaseFromNotesTask.fs).
- Interactive `promptYesNo` gates inside FAKE targets auto-accept when the `CI` env var is `true` (see [`build/MessagePrompts.fs`](build/MessagePrompts.fs)). CI sets this; humans get prompted.
- The NuGet API key is read from the `NUGET_API_KEY` env var by FAKE; CI passes it through from the `NUGET_API_KEY` GitHub Actions secret.

**When changing release behavior:** edit the FAKE task, not the workflow. If a workflow file starts growing shell logic (`grep`/`sed`/`awk` against repo files, conditional `if: ...` chains around build steps), that logic should move into a FAKE target.

## Things to avoid

- Do not add an fsdocs / FsDocs site here — usage examples live in the base BioFSharp docs.
- Do not change `TargetFramework` away from `netstandard2.0` for the shipped projects. `BioFSharp.INSDC.Crawler` is the sole exception (net8.0) because FsHttp needs .NET 6+; it is still packed and published to NuGet like the rest.
- Do not bypass the generator by hand-writing C# types under `BioFSharp.FileFormats.INSDC`.
- Do not fetch test fixtures from the network at test time. Download once from `https://www.ebi.ac.uk/ena/browser/api/xml/<ACCESSION>` and commit under `tests/fixtures/`. The crawler tests follow this too: they stub `Fetch` against committed fixtures, and the live path is opt-in via `INSDC_LIVE_TESTS=1`.
- Do not wire `regenerateInsdcTypes` into the default build — generated code is committed precisely so day-to-day builds don't require the tool.

## Pointers

- Authoritative plan: [`plans/implementation.md`](plans/implementation.md)
- Upstream schemas: <https://ftp.ebi.ac.uk/pub/databases/ena/doc/xsd/sra_1_5/>
- ENA record API (for fixtures): `https://www.ebi.ac.uk/ena/browser/api/xml/<ACCESSION>`
- ENA Portal API (crawler discovery): `https://www.ebi.ac.uk/ena/portal/api/filereport?accession=<ACCESSION>&result=read_run`
- Parent project: <https://github.com/CSBiology/BioFSharp>
- Generator tool: <https://www.nuget.org/packages/dotnet-xscgen>
