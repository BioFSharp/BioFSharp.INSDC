# BioFSharp.INSDC

`BioFSharp.INSDC` reads, writes, crawls, stores, and maps metadata from the
[International Nucleotide Sequence Database Collaboration](https://www.insdc.org/).
The repository ships five NuGet packages built around the ENA/SRA 1.5 XML schemas.

## Package boundaries

| Package | Target | Responsibility |
| --- | --- | --- |
| `BioFSharp.FileFormats.INSDC` | `netstandard2.0` | Generator-owned C# XML types and generated fragment selectors. Most applications consume these through the IO package. |
| `BioFSharp.IO.INSDC` | `netstandard2.0` | Idiomatic F# XML read/write modules, instance-aware XPath/XPointer lookup, and structural inspection helpers. |
| `BioFSharp.INSDC.ArcIR` | `netstandard2.0` | The current proof-of-concept INSDC-to-ArcIR property-graph mapping and derived text, GraphML, and HTML inspection views. |
| `BioFSharp.INSDC.SQLite` | `netstandard2.0` | A versioned normalized SQLite store for BioProject, Study, BioSample, Experiment, Run, and accession relations. |
| `BioFSharp.INSDC.Crawler` | `net8.0` | A packaged ENA crawler and raw-artifact collector. It uses .NET 8 because its HTTP dependency requires .NET 6 or later. |

The crawler is the only target-framework exception. It is still packed and
published with the other projects.

## IO surface

Each supported XML entity module exposes the same four core functions:

```fsharp
BioProject.read       : path:string -> BioProjectSet
BioProject.readString : xml:string -> BioProjectSet
BioProject.write      : path:string -> BioProjectSet -> unit
BioProject.writeString: BioProjectSet -> string
```

Equivalent modules exist for Study, BioSample, Experiment, Run, Analysis,
Submission, and Receipt. XPath/XPointer and structural-ontology helpers describe
the source XML structure. They are useful for inspection and provenance work,
but they are not the semantic model of the ArcIR mapping.

## Current ArcIR terminology

The `BioFSharp.INSDC.ArcIR` package is a useful proof of concept: explicit
per-entity converters create typed objects, relations, properties, and
annotations. Its GraphML, HTML, and text renderers are derived inspection tools.
The embedded HTML renderer is not the intended final workbench architecture.

The authoritative [implementation plan](plans/implementation.md) uses these
terms for the next architecture:

- **F1** ingests source artifacts and semantic mappings into one canonical,
  target-neutral ArcIR state.
- **Curation activities** create later, revisioned states without rewriting
  historical reports.
- **F2** compiles a selected state into target artifacts without mutating the
  IR.

R1 and R2 are raw-artifact readiness formats materialized by the crawler. They
are not F2 implementations and do not require ArcIR.

## SQLite and crawling

`BioFSharp.INSDC.SQLite.Schema.init` creates a new database or applies ordered
forward migrations. Normal connections enforce foreign keys. The crawler opts
into `AllowCrawlerSoftReferences` explicitly because ENA can refer to the same
sample through different archive accessions and partial crawls may intentionally
leave those references dangling. Public insert and delete operations are
transactional.

Crawler operations are strict about exhausted batch failures by default. Set
`CrawlOptions.ContinueOnPartialFailure = true` only for an explicit best-effort
inspection crawl. Text and binary fetch functions remain injectable for offline
tests. Downloaded artifacts are validated where possible, written atomically,
and reused on resume only when an existing file is valid.

The crawler can materialize:

- typed records in memory or in the SQLite store;
- a round-tripped INSDC XML tree;
- paper full text as JATS XML or PMC Open Access PDF;
- DEE2 count bundles;
- composed raw R1 and R2 directory layouts.

## Build and test

The repository pins its SDK in `global.json` and local tools in
`.config/dotnet-tools.json`.

```bash
dotnet tool restore
./build.sh                 # BuildSolution
./build.sh runTests        # full offline suite + security and drift gates
./build.sh pack
```

On Windows use `build.cmd`, `build.cmd runTests`, and `build.cmd pack`.
Live endpoint tests remain opt-in through `INSDC_LIVE_TESTS=1`.

`RunTests` includes two repository gates:

- `DependencyAudit` fails for an unsuppressed transitive vulnerability. Any
  temporary exception must be an exact, reviewed, expiring entry in
  `build/dependency-audit-suppressions.json`; expired and unused entries fail.
- `VerifyGeneratedArtifacts` regenerates into a temporary directory and fails
  on byte-level drift without modifying the working tree.

## Generated artifacts

Generated C# under `src/BioFSharp.FileFormats.INSDC/Generated/` is never edited
by hand. Friendly names are controlled only by
`schemas/typename-substitutions.txt`.

After changing schemas, generator flags, or substitutions, run the generators in
dependency order:

```bash
./build.sh regenerateInsdcTypes
./build.sh generateFragmentSelectors
./build.sh generateStructuralOntology
./build.sh verifyGeneratedArtifacts
```

Use the corresponding `build.cmd` targets on Windows and commit every generated
change. Inputs are sorted, generator headers are canonicalized, and committed
text output uses UTF-8 without a BOM and LF line endings.

## Repository layout

```text
build/                                  FAKE build, audit, generator, and release targets
plans/implementation.md                 authoritative roadmap
src/BioFSharp.FileFormats.INSDC/        generated C# type model and schemas
src/BioFSharp.IO.INSDC/                 F# XML IO and structural inspection
src/BioFSharp.INSDC.ArcIR/              current INSDC ArcIR proof of concept
src/BioFSharp.INSDC.SQLite/             versioned SQLite store
src/BioFSharp.INSDC.Crawler/            net8.0 crawler and raw-artifact formats
tests/BioFSharp.INSDC.Tests/             offline tests and committed fixtures
docs/                                   placeholder; usage documentation lives upstream
```

Released under the MIT license.
