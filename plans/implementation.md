# BioFSharp.INSDC implementation plan

## Overview

This repo ships two packages that together give BioFSharp users an INSDC (International Nucleotide Sequence Database Collaboration) read/write surface:

- **`BioFSharp.FileFormats.INSDC`** — a **C#** library whose types are auto-generated from the official ENA XSD schemas via [`dotnet-xscgen`](https://www.nuget.org/packages/dotnet-xscgen) (the `XmlSchemaClassGenerator` CLI). It is C# rather than F# because there is no F# equivalent of `XmlSchemaClassGenerator`.
- **`BioFSharp.IO.INSDC`** — an F# library that wraps the generated C# type model with idiomatic `read` / `readString` / `write` / `writeString` functions per INSDC entity.

The naming mirrors the base `BioFSharp` namespace layout so both packages can be picked up directly as dependencies of `BioFSharp`. Both target `netstandard2.0` for parity with the rest of the BioFSharp ecosystem.

## 1. [x] Pre-flight cleanup (template residue)

The repo was scaffolded from the `BioFSharp.XYZ` template and still carries placeholder content that must be cleared before the real work lands:

- `README.md` — replace the template content with an INSDC-specific README.
- `RELEASE_NOTES.md` — seed an initial entry, e.g. `0.0.0-preview.1`.
- Both `src/**/*.fsproj` files contain `BioFSharp.XYZ` in `PackageProjectUrl`, `RepositoryUrl`, `FsDocsLicenseLink`, and `FsDocsReleaseNotesLink` — retarget every URL to `BioFSharp.INSDC`. Fill in `Authors`, `Description`, `Summary`.
- Delete the template `Library.fs` in both `src/` projects.
- `tests/BioFSharp.INSDC.Tests/Tests.fs` still references `BioFSharp.XYZ.BioTalk` — drop the test, replace with INSDC tests (section 5).
- `docs/index.fsx` is left as-is (no docs site for this repo). Confirm `docs/img/icon.png` exists so `pack` does not fail.

## 2. [x] `BioFSharp.FileFormats.INSDC` (C# type model)

### Project conversion (F# → C#)

- Rename `src/BioFSharp.FileFormats.INSDC/BioFSharp.FileFormats.INSDC.fsproj` → `.csproj`.
- Switch SDK config to a standard C# project: `<Project Sdk="Microsoft.NET.Sdk">`, drop `<Compile Include="Library.fs" />`.
- Keep `<TargetFramework>netstandard2.0</TargetFramework>`, `<GenerateDocumentationFile>true</GenerateDocumentationFile>`, the symbol package settings, and the icon `<None Include="..\..\docs\img\icon.png" .../>`.
- Update `BioFSharp.INSDC.slnx` to reference the new `.csproj` path.

### Generator tool install (local, committed)

The generator is the `dotnet-xscgen` package. Install it **locally** so the version is pinned and reproducible:

```bash
dotnet new tool-manifest          # one-time, creates .config/dotnet-tools.json
dotnet tool install dotnet-xscgen --version 3.0.1270
```

Commit `.config/dotnet-tools.json`. Contributors run `dotnet tool restore` once after clone. Invoke the tool as `dotnet xscgen ...` from the repo root.

### Schema sourcing

Download the XSDs from <https://ftp.ebi.ac.uk/pub/databases/ena/doc/xsd/sra_1_5/> (latest available — `sra_1_6` does not exist). Commit them under `src/BioFSharp.FileFormats.INSDC/schemas/` so generation is reproducible offline.

All schemas present in `sra_1_5/` are in scope for v1. At minimum:

- `SRA.project.xsd` (BioProject)
- `SRA.study.xsd`
- `SRA.sample.xsd` (Sample / BioSample)
- `SRA.experiment.xsd`
- `SRA.run.xsd`
- `SRA.analysis.xsd`
- `SRA.submission.xsd`
- `SRA.receipt.xsd`
- `SRA.common.xsd` (shared include)

Pull the whole directory rather than cherry-picking, so any siblings (e.g. EGA-specific schemas) are also covered.

### Generator wiring (on-demand only)

Do **not** wire generation into every build — the generated `.cs` files are committed and compile on their own. Instead add a single FAKE target `regenerateInsdcTypes` (in `build/BasicTasks.fs`, or a new `build/GeneratorTasks.fs`) that:

- wipes `src/BioFSharp.FileFormats.INSDC/Generated/`
- invokes `dotnet xscgen` against every `.xsd` in `src/BioFSharp.FileFormats.INSDC/schemas/`
- outputs into `src/BioFSharp.FileFormats.INSDC/Generated/`
- maps every xsd → namespace `BioFSharp.FileFormats.INSDC`
- documents the chosen generator flags in a comment on the target (e.g. `--integer-type=System.Int64`, nullable-element handling)

Contributors run `build.cmd regenerateInsdcTypes` (or `build.sh regenerateInsdcTypes`) **only when schemas change**. `buildSolution` does *not* depend on this target.

## 3. [x] `BioFSharp.IO.INSDC` (F# IO wrapper)

### Module files

Replace the template `Library.fs` with one F# source file per INSDC entity:

- `BioProject.fs`
- `Study.fs`
- `Sample.fs`
- `Experiment.fs`
- `Run.fs`
- `Analysis.fs`
- `Submission.fs`
- `Receipt.fs`
- `Internal/XmlSerializer.fs` — shared helper wrapping `System.Xml.Serialization.XmlSerializer` for generic `read` / `readString` / `write` / `writeString`.

### Public API

Namespace: `BioFSharp.IO.INSDC`. INSDC files are XML, so there is no `readLines` (XML is not line-based). Every entity module exposes the same four functions:

```fsharp
module BioProject =
    /// Read INSDC project XML records from disk.
    val read        : filePath: string -> seq<ProjectType>
    /// Parse INSDC project XML records from an in-memory string.
    val readString  : xml: string -> seq<ProjectType>
    /// Write an INSDC project to disk as XML.
    val write       : filePath: string -> project: ProjectType -> unit
    /// Serialize an INSDC project to an XML string.
    val writeString : project: ProjectType -> string
```

`ProjectType` is the C# type emitted by the generator, re-exported via a type alias so consumers do not need to `open BioFSharp.FileFormats.INSDC`. Same shape for set-backed entities (`BioProject`, `Study`, `Sample`, `Experiment`, `Run`, `Analysis`, `Submission`): `read` and `readString` return `seq<_>` because ENA responses commonly use `*_SET` roots. `Receipt` remains single-record because there is no generated `ReceiptSet` type.

### Conventions

- All public members carry `///` XML doc comments.
- `BioFSharp.IO.INSDC.fsproj` adds a `<ProjectReference>` to `BioFSharp.FileFormats.INSDC.csproj`.
- Project metadata (`Authors`, `Description`, repo URLs) retargeted to `BioFSharp.INSDC`.

## 4. [x] Tests (`BioFSharp.INSDC.Tests`)

State: fixture-based coverage is complete for every IO module. The old smoke test was replaced with deep object-graph roundtrip tests against committed ENA fixtures.

### Test files

One test module per IO module:

- Done in `Tests.fs`: `BioProject`, `Study`, `BioSample`, `Experiment`, `Run`, `Analysis`, `Submission`, `Receipt`.
- Future cleanup: split the current `Tests.fs` into one test module/file per IO module if desired.

Each module covers three cases:

1. **Read** — load a committed ENA XML fixture and assert a handful of key field values.
2. **Roundtrip** — `read >> write >> read` produces a structurally equal value. Pick one comparison strategy (deep object-graph equality is simpler than XML canonicalisation) and document it inline.
3. **`read` / `readString` parity** — parsing the same XML via file vs. string produces equal results.

Keep xunit (already wired). Drop the existing `BioTalk` test.

### Fixtures

Real ENA records, downloaded once and committed:

- Current path: `tests/fixtures/<accession>.xml`
- Source URL pattern: `https://www.ebi.ac.uk/ena/browser/api/xml/<ACCESSION>` (download by hand; do **not** fetch at test time)
- Committed accessions: `PRJDB5192`, `DRP003416`, `SAMD00064197`, `DRX066772`, `DRR072834`, `ERZ496533`, `DRA005154`.
- `receipt-sample.xml` is hand-crafted because `RECEIPT` is a submission-API response, not a stored record. Shape mirrors the ENA programmatic submission guide example.
- Source URLs and download date are recorded in `tests/fixtures/README.md`.

Tests load fixtures off disk via a small relative-path helper. No network at test time.

## 5. [x] Build / CI touch-ups

- Confirm `build/ProjectInfo.fs` `solutionFile` resolves (already `BioFSharp.INSDC.slnx`).
- Verify `build.cmd` / `build.sh` entry points still work after the `.fsproj` → `.csproj` swap in the slnx.
- Add the `regenerateInsdcTypes` target. It must not be chained into the default build — generated code is committed precisely so contributors can build without the tool restored.
- Audit `.github/workflows/build-and-test.yml` for template residue: SDK pin and the Codecov slug.

The `regenerateInsdcTypes` target lives in `build/BasicTasks.fs`. It is standalone (no dependencies on `clean` / `buildSolution`) and is not referenced by any release pipeline.

CI uses `global-json-file: global.json` so the workflow tracks the SDK version pinned at the repo root (currently 10.0.100). The Codecov slug is retargeted to `BioFSharp/BioFSharp.INSDC`.

## 6. [x] Verification

Each step gates the next:

1. [x] `dotnet tool restore` succeeds (verified 2026-05-23 in devcontainer; restores `dotnet-xscgen` 3.0.1270 and `fsdocs-tool` 20.0.1).
2. [x] `build.sh regenerateInsdcTypes` produces `.cs` files under `src/BioFSharp.FileFormats.INSDC/Generated/` (verified 2026-05-23; only diff vs. committed output is the absolute-path noise in the generator-header comment).
3. [x] `build.sh` (default `buildSolution`) succeeds with zero `CS1591` (missing-XML-doc) warnings (verified 2026-05-23; `0 Error(s)`).
4. [x] `bash build.sh runtests` passes locally in the devcontainer (`24/24` tests, re-confirmed 2026-05-23).
5. [x] `build.sh pack` produces both nupkgs with non-template metadata (verified 2026-05-23 — `pkg/BioFSharp.FileFormats.INSDC.0.1.0.nupkg` and `pkg/BioFSharp.IO.INSDC.0.1.0.nupkg`). The `Pack` target uses an interactive `Y/n` confirmation prompt; pipe `echo Y |` when invoking non-interactively.

## 7. Out of scope

- No fsdocs site for this repo; usage examples live in the base `BioFSharp` docs.

## Dos

- ALWAYS add XML documentation to all public types and functions in both projects.
- ALWAYS run the FAKE build targets (`build.cmd` / `build.sh`) for solution-wide work — they are the source of truth for build/test/pack configuration. Use raw `dotnet build` / `dotnet test` only when iterating on a single project.

## Don'ts

- Do not hand-edit anything under `src/BioFSharp.FileFormats.INSDC/Generated/` — it is regenerated from XSDs.
- Do not chain `regenerateInsdcTypes` into the default build.
- Do not add an fsdocs site to this repo.
