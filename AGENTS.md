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
.NET 6+, so it is **net8.0** with `IsPackable=false` (kept out of `pack`).

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
- Do not change `TargetFramework` away from `netstandard2.0` for the shipped projects. The `BioFSharp.INSDC.Crawler` dev tool is the sole exception (net8.0, `IsPackable=false`) because FsHttp needs .NET 6+.
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
