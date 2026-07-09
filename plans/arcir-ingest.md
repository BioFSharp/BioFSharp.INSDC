# Ingesting supplementary sources (paper + count data) into the ArcIR

## Context

The forward mapping turns INSDC records into the ArcIR property graph (`Arc.Build.ArcIR` — typed
`ArcObject` nodes + `ArcRelation` triples; see [arcir-mapping.md](arcir-mapping.md)). Before writing
the reverse "to a richer target" functions, we raise the **metadata quality** of the IR by folding in
two sources INSDC records don't carry:

- a **scientific paper** (PDF or XML) that describes the dataset, and
- a **zip/folder of count data** whose file headers are run accessions.

This is cheap because the mapping already has the right seam. Every per-accession converter emits a
`ConversionResult` (`{ Objects; Relations; Pending }`); `Mapping.build` folds many of those into one
graph and resolves each `PendingRelation` **by accession, leaving an edge dangling until the target
record is merged in** (`Mapping.resolveTarget`). So the two new sources are just two more producers of
`ConversionResult` fragments keyed by accession — no new graph model, and the dangling-relation
behaviour we want is the default.

**Scope**

- Paper → a **Resource node for the file**, plus **extracted authors** as person `Agent` nodes. No
  full-text/JATS-body modelling; no cited-accession mining beyond the related accession(s) the caller
  supplies. Authors are extracted from JATS XML; a PDF is ingested as a Resource node only (PDF author
  extraction is deferred — it needs a PDF library).
- Count data → **a Resource node per file**, with **a fragment (sub-node + edge) per column**, each
  column keyed to the run accession in its header. Only the header line is parsed (no matrix body).
- The reverse/target-emitting functions are out of scope here.

## Fragment selectors (count columns)

The repo already has a fragment-selector convention: XML properties are addressed with W3C XPointer
(`#xpointer(/PROJECT/NAME)` — see [xml-fragment-selectors.md](xml-fragment-selectors.md)), and that
doc names **RFC 7111** as the CSV analogue (`file.csv#col=1`). Each count **column fragment** is
addressed with the correct RFC 7111 syntax — `<fileId>#col=<n>`, where `<n>` is the **1-based column
position** in the file (position-based, not name-based, per RFC 7111). The feature/gene-id column is
`col=1`, so a run in the k-th data column is `col=k+1`. This makes a column node's id a real,
resolvable CSV fragment identifier into the source file, exactly parallel to the XML `#xpointer(...)`
ids.

## Design

Everything lives in the existing **`BioFSharp.INSDC.ArcIR`** project (netstandard2.0, no new NuGet
dependency — zip via in-box `System.IO.Compression.ZipArchive` over a `FileStream`; JATS via in-box
`System.Xml.Linq`, already used in `GraphMl.fs`). A new `Ingest/` folder sits parallel to `Mapping/`.

Core builders are **pure** (already-read descriptors/records → `ConversionResult`), mirroring the
repo's split between format reading and graph building; thin disk-touching convenience readers sit on
top, so the builders are unit-testable offline.

### Reused, unchanged

- `Arc.Build.ArcObject.create` / `ArcRelation.create` — node/edge construction.
- `ConversionResult` / `PendingRelation` / `Mapping.resolveRelations` / `Mapping.build`
  (`Mapping/Mapping.fs`) — fragment type + two-pass assembly + dangling resolution.
- `Convert.pendingAccession` (`Mapping/Convert.fs`) — paper→dataset pending edges.
- `Annotations.stringField` (`Mapping/Annotations.fs`) — scalar → annotation (house style: values go
  in `Annotations`, not `Properties`).
- The `agent:<email|name>` dedup id scheme in `Mapping/SubObjects.fs` — so a paper author and an INSDC
  contact with the same email collapse to one enriched `Agent` node via `ArcIR.addObject`'s
  merge-on-id. The private `agentNode` is factored into a reusable `SubObjects.person` builder.
- The FASTQ-fragment precedent in `playground/crawl_arcir.fsx` — the "add non-XML nodes+edges keyed by
  run accession" pattern the count data follows.

### Vocabulary additions — `Vocabulary.fs`

Under the existing `BaseIri`; each is locked by a regression test:

- `DType.publication` — the paper Resource.
- `DType.countMatrix` — a count/expression matrix file.
- `DType.countColumn` — one column (per-run expression profile).
- `Rel.hasColumn` — countMatrix file → countColumn.

Reused: `Rel.hasContact` (paper → author), `Rel.references` (paper → dataset, pending),
`Rel.producesData` (run → column), `ArcObjectKind.Resource`, `DType.data`, `DType.agent`/`person`.

### New files (appended to the `.fsproj` after `Mapping\INSDC.fs`)

- **`Ingest/Types.fs`** — hand-written records:
  - `ResourceFile = { Name; ByteSize; Checksum; MediaType }` (file metadata, shared).
  - `PaperAuthor = { Name; Email; Affiliation; Orcid }`, `PaperMetadata = { Title; Doi; Journal; Authors }`.
  - `CountColumn = { Index; RunAccession }` (`Index` = 1-based CSV column position, RFC 7111) and
    `CountFile = { File: ResourceFile; Columns: CountColumn list }`.
- **`Ingest/Paper.fs`** — `Paper.convert : PaperMetadata -> ResourceFile -> relatedAccessions -> ConversionResult`:
  paper node (`Kind = Resource`, `DTypes = [publication]`, id `doi:<doi>` else `paper:<name>`; Title/DOI/Journal
  annotations; file metadata as Properties) + author `Agent` nodes (deduped) with `hasContact` edges +
  `references` pending edges to the related accessions.
- **`Ingest/CountData.fs`** — `CountData.convert : CountFile -> ConversionResult` (+ `convertMany`):
  file node (`Kind = Resource`, `DTypes = [countMatrix; data]`, id `count:<name>`) + per column a fragment
  node id `<fileId>#col=<index>` (`Kind = Resource`, `DTypes = [countColumn]`, Properties `Column`,
  `RunAccession`, `FragmentSelector = "#col=<index>"`), a `file --hasColumn--> column` edge, and a direct
  `run --producesData--> column` edge (subject is the run accession, so it dangles until the run node is
  merged). `Kind = Resource` (not the reserved `Selector` kind) keeps `producesData`/`hasColumn`
  consistent with the FASTQ/`analysisFile` precedent; the RFC 7111 fragment lives in the id + property.
- **`Ingest/Readers.fs`** — disk-touching convenience: `describeFile`, `readJats` (System.Xml.Linq:
  `article-title`, `contrib[@contrib-type=author]`, `name/given-names+surname`, `email`, `aff`,
  `article-id[@pub-id-type=doi]`), `readCountArchive`/`readCountFolder` (zip via `ZipArchive` or folder
  walk; read only the header line; keep each header cell matching `^[SED]RR\d+$` with its 1-based index).
- **`Ingest/Ingest.fs`** — facade: `Ingest.paper`, `Ingest.countData` (pure); `Ingest.paperFromJats`,
  `Ingest.countDataFromArchive` (readers + builders); `Ingest.incorporate (existing) (results)` — add
  new objects then resolve pending against the union of existing + new objects and add the edges.

## Tests & fixtures

Fixtures under `tests/fixtures/` (committed, offline, documented in its README):

- `paper-PRJDB5192.jats.xml` — minimal JATS: title, DOI, 2 authors (name/email/aff).
- `counts-PRJDB5192.tsv` — header `gene_id\tDRR072834\t…` + a couple of rows.
- `counts-PRJDB5192.zip` — the TSV zipped, to exercise the archive reader.

Tests in `tests/BioFSharp.INSDC.Tests/Tests.fs` (xUnit, mirroring `ArcMappingTests`):

- `IngestPaperTests` — `readJats` extracts title/DOI/2 authors; `Ingest.paper` yields 1 `publication`
  Resource + 2 person `Agent`s (`hasContact`) + pending `references` edges; after `incorporate` into a
  small INSDC IR the reference edge resolves onto the real project node.
- `IngestCountDataTests` — header parse yields the run accessions with correct 1-based indices;
  `Ingest.countData` yields 1 `countMatrix` Resource + N `countColumn` nodes whose ids/`FragmentSelector`
  use the exact RFC 7111 form (`…#col=2` / `…#col=3`) + N `hasColumn` + N `producesData` edges; the zip
  reader returns the same `CountFile` as the loose TSV.
- Author-merge — an author email equal to an INSDC contact email collapses to one `Agent` after
  `incorporate`.
- `IngestVocabularyTests` — assert the new `DType.*`/`Rel.*` IRIs (regression lock).

No CI/workflow changes (CI is a thin shell around FAKE); the test project is already registered.

## Verification

1. `./build.sh` then `./build.sh runtests`.
2. `playground/ingest.fsx`: build the INSDC IR from fixtures, `Ingest.paperFromJats` +
   `Ingest.countDataFromArchive`, `Ingest.incorporate`, `Html.writeFile`; open `ingest.html` and check
   the paper node + author agents + `references` edge into the project, the count file + per-column
   nodes + `producesData` edges from run nodes, and a `Missing` placeholder for any unmerged accession.

## Out of scope (deferred)

- PDF author extraction (needs a PDF library such as PdfPig). PDF files ingest as Resource nodes now.
- Parsing the count matrix body (dimensions, feature list, 10x MTX triplets, normalization).
- The reverse functions (IR → ARC/ISA, RO-Crate, JSON/RDF export).
