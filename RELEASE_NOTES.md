### 0.2.1 (2026-07-09)

Crawler fixes surfaced by large-scale crawls (incl. ENA umbrella projects such as `PRJNA9506`, which fans out to thousands of child projects).

- **BioFSharp.INSDC.Crawler** — a project/study with no sequencing runs now persists its own record: discovery is run-driven, so a childless root previously discovered nothing (not even itself). `Discovery.withRoot` seeds the root into the bucket its accession prefix implies (`PRJ…` → BioProject, `SRP`/`ERP`/`DRP…` → Study).
- **BioFSharp.INSDC.Crawler** — new `Started` crawl event, emitted before discovery, so the first log line identifies the root accession rather than only learning it at `done —`.
- **BioFSharp.INSDC.SQLite / Crawler** — bulk insert: `Sql.withTransaction` is now reentrant (a nested call joins the active transaction rather than throwing, since SQLite has no nested transactions), letting the crawler persist an entire crawl in a single transaction instead of one commit per record — the difference between a trickle and a bulk load on crawls of hundreds of thousands of runs.

### 0.2.0 (2026-07-09)

Expands the suite from two packages to five: `BioFSharp.FileFormats.INSDC` and
`BioFSharp.IO.INSDC` gain new capabilities, while `BioFSharp.INSDC.SQLite`,
`BioFSharp.INSDC.ArcIR`, and `BioFSharp.INSDC.Crawler` ship for the first time.

- **BioFSharp.FileFormats.INSDC** — generated per-type XPointer/XPath fragment selectors (`FragmentSelectors.cs`) so individual elements of a record can be addressed by fragment identifier.
- **BioFSharp.IO.INSDC** — structural ontology that decompiles records into ontology term/value pairs whose term names mirror the XML structure; fragment-selector tracking via per-instance `xpathOf` (bare XPath) and `xpointerOf` (`#xpointer`) lookups plus an `xpathEntries` DTO.
- **BioFSharp.INSDC.SQLite** *(new)* — SQLite-backed store that deconstructs BioProject, Study, BioSample, Experiment, and Run records into a normalized schema and reconstructs them on read, with per-entity modules and an `accession_relations` table capturing the cross-record connectivity graph.
- **BioFSharp.INSDC.ArcIR** *(new)* — maps INSDC records into ArcIR, an ARC-oriented intermediate representation (a property graph of typed, annotations-first objects and relations) with sample references resolved to their BioSample node; renders the graph to GraphML, interactive HTML, and text; ingests supplementary papers and count data.
- **BioFSharp.INSDC.Crawler** *(new)* — crawls a project accession from ENA (Portal `filereport` discovery to Browser API fetch) and persists every connected run, experiment, sample, and study via the SQLite store plus its connectivity table; exposes `crawl` / `crawlToSqlite` (with `*Async` / `*WithAsync` variants); targets net8.0 because FsHttp requires .NET 6+, published to NuGet like the rest.

### 0.1.0 (2026-05-21)

initial release of the generated C# type model and F# wrapper for the INSDC file formats.
The type model covers all schemas from https://ftp.ebi.ac.uk/pub/databases/ena/doc/xsd/sra_1_5, but IO only supports the most common ones for now:

- BioProject
- Study
- BioSample
- Experiment
- Run
- Submission
- Analysis

### 0.0.0-preview.1 (Unreleased)

* Initial repository scaffolding for BioFSharp.INSDC.
* Two-package layout established: `BioFSharp.FileFormats.INSDC` (C# generated type model) and `BioFSharp.IO.INSDC` (F# wrapper).
