### 0.2.0 (2026-07-09)

Expands the suite from two packages to five. `BioFSharp.FileFormats.INSDC` and
`BioFSharp.IO.INSDC` gain new capabilities; `BioFSharp.INSDC.SQLite`,
`BioFSharp.INSDC.ArcIR`, and `BioFSharp.INSDC.Crawler` ship for the first time.

**BioFSharp.FileFormats.INSDC**
- Generated per-type XPointer/XPath fragment selectors (`FragmentSelectors.cs`) so
  individual elements of a record can be addressed by fragment identifier.

**BioFSharp.IO.INSDC**
- Structural ontology: decompile records into ontology term/value pairs whose term
  names mirror the XML structure (`Internal/StructuralOntology.fs` + `StructuralOntology.obo`).
- Fragment-selector tracking: per-instance `xpathOf` (bare XPath) and `xpointerOf`
  (`#xpointer`) lookups, plus an `xpathEntries` DTO for backend use.

**BioFSharp.INSDC.SQLite** *(new)*
- SQLite-backed store that deconstructs BioProject, Study, BioSample, Experiment, and
  Run records into a normalized schema and reconstructs them on read.
- Per-entity public modules and an `accession_relations` table that captures the
  cross-record connectivity graph.

**BioFSharp.INSDC.ArcIR** *(new)*
- Maps INSDC records into ArcIR, an ARC-oriented intermediate representation (a property
  graph of typed, annotations-first objects and relations), with sample references
  resolved to their BioSample node.
- GraphML, interactive HTML, and text renderers of the graph.
- Ingest of supplementary papers and count data into the IR.

**BioFSharp.INSDC.Crawler** *(new)*
- Crawls a project accession from ENA (Portal `filereport` discovery to Browser API
  fetch) and persists every connected run, experiment, sample, and study via the
  `BioFSharp.INSDC.SQLite` store plus its connectivity table.
- `crawl` / `crawlToSqlite` entry points (with `*Async` / `*WithAsync` variants).
- Targets net8.0 because FsHttp requires .NET 6+; published to NuGet like the rest.

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
