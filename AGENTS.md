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
│   └── BioFSharp.IO.INSDC/                 F# wrapper (.fsproj), one module per INSDC entity
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

## Things to avoid

- Do not add an fsdocs / FsDocs site here — usage examples live in the base BioFSharp docs.
- Do not change `TargetFramework` away from `netstandard2.0` for the shipped projects.
- Do not bypass the generator by hand-writing C# types under `BioFSharp.FileFormats.INSDC`.
- Do not fetch test fixtures from the network at test time. Download once from `https://www.ebi.ac.uk/ena/browser/api/xml/<ACCESSION>` and commit under `tests/BioFSharp.INSDC.Tests/fixtures/`.
- Do not wire `regenerateInsdcTypes` into the default build — generated code is committed precisely so day-to-day builds don't require the tool.

## Pointers

- Authoritative plan: [`plans/implementation.md`](plans/implementation.md)
- Upstream schemas: <https://ftp.ebi.ac.uk/pub/databases/ena/doc/xsd/sra_1_5/>
- ENA record API (for fixtures): `https://www.ebi.ac.uk/ena/browser/api/xml/<ACCESSION>`
- Parent project: <https://github.com/CSBiology/BioFSharp>
- Generator tool: <https://www.nuget.org/packages/dotnet-xscgen>
