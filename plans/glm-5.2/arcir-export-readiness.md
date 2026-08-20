# ArcIR → AI-readiness export plan (F2)

> Refines `plans/arcir-export-readiness.md` with the three locked decisions from the 2026-07-10 design
> session. Focuses on **R0** as the implementation baseline; R1–R4 are sketched to fix the shape. Each
> AI readiness level format gets its own detail pass before implementation.
>
> **Superseded in part (2026-07-13).** The canonical plan is now
> [`plans/claude/arcir-export-readiness.md`](../claude/arcir-export-readiness.md). R1 and R2 have since
> been redefined as raw-artifact formats and built in the crawler — see the R1/R2 sections below.

## Overview

The F2 export functions take a built `ArcIR` graph and **materialize a dataset folder** in one of five
**AI readiness level formats**, each carrying more metadata and structure than the last. Same input, same
project, progressively more context emitted around the same primary data (a count matrix):

| Format | Name | One-liner |
|---|---|---|
| **R0** | Plain data | Count matrix in a named folder, sample-labelled headers, nothing else. |
| **R1** | Un/weakly structured | The DEE2 count archive verbatim + the paper; source is *obscured* (no INSDC XML). |
| **R2** | Structured standard | The full INSDC XML record tree in a folder layout + counts (+ paper). |
| **R3** | Ontology-backed | The full ArcIR itself (the graph is already ontology-annotated). |
| **R4** | FAIR digital object | An ARC in the new non-ISA YAML form: investigation + assay/sample provenance. |

Each AI readiness level format is one function `ArcIR -> … -> ExportTree` (plus a thin `writeTo dir` sink). This mirrors the
existing serializers (`GraphMl.toString`/`writeFile`, `Html.toString`/`writeFile`): a pure transform to an
in-memory artifact, and a separate disk sink, so the transform is unit-testable without touching the
filesystem.

## Locked decisions (from 2026-07-10 clarifying session)

| Decision | Resolution |
|---|---|
| **Home** | `Export/` layer inside `BioFSharp.INSDC.ArcIR`, alongside `GraphMl.fs` / `Html.fs`. No new project. |
| **Project short name** | Slugified BioProject `<TITLE>` only. **Never** `<NAME>` (bad input), **never** accession. No fallback — error if no `Title` annotation. |
| **Sample name** | `sample_name` attr → `<TITLE>` → alias → accession. Collision or any unmapped column → `S_0…S_N` for all columns. |
| **Count body source** | Pass count file path(s) to the F2 function. The IR keeps only matrix identity + column→run mapping; the body is re-read from a caller-supplied source. |

## Constraints discovered (these shape every format)

1. **The IR is metadata-only. Count *values* are not in it.** `Ingest/Readers.fs` parses only the *header
   line* of a count matrix: it keeps run-accession columns as `CountColumn { Index; RunAccession }` and
   drops everything else (including the `gene_id` feature column, which fails the run-accession regex). The
   cell matrix never enters the graph. **Consequence:** any format that emits the count file (R0, R2, R4)
   must re-read the raw bytes from a caller-supplied source, and use the IR only to *rewrite the header* and
   *choose the folder/name*. The export signature therefore carries a `CountMatrixSource` alongside the IR.

2. **The IR is multi-project.** An umbrella root can fan out to thousands of unrelated child projects
   (`project_ena_umbrella_projects`). Export is therefore **per-BioProject**: pick one `BioProject` node,
   compute its sub-graph, emit one folder. A whole-IR export is just a map over BioProject nodes.

3. **No first-class BioProject↔Study edge.** `Study` carries the project only as a numeric `ProjectId`
   *annotation*; `BioProject` emits no edge to its studies. So scoping a count column to a project cannot be
   done by a pure edge-walk today. See *Enabling change* below — R0 sidesteps it via the single-project
   assumption; R2+ will want the edge.

4. **Human names live in annotations, not properties.** `BioProject`/`BioSample`/`Study` put
   Title/Name/Description/Alias in `Annotations` (via `Annotations.stringField`), while `nodeLabel`
   (`GraphText.fs`) reads only `Properties` and thus falls back to the accession. The export needs its own
   annotation-aware name lookup, not `nodeLabel`.

5. **Node identity is the accession.** `Convert.entityId` keys every entity node by accession (alias
   fallback). Count nodes are keyed `count:<file>` and `count:<file>#col=<n>`.

6. **`sample_name` is an attribute, not a scalar.** The BioSample converter (`BioSampleConversion.convert`)
   emits `<TITLE>` as the `Title` annotation and `<SAMPLE_ATTRIBUTE><TAG>sample_name</TAG>` as an
   `INSDC attribute` annotation whose term Name is `sample_name`. There is no dedicated `SampleName`
   property; the organism descriptor (`<SAMPLE_NAME>`) is a separate `hasOrganism` sub-object, not a label.
   The precedence chain must look up annotations by term Name.

7. **Alias and accession collapse into the ArcId.** `Convert.entityId` keys every node by accession, falling
   back to alias when accession is absent. So the "alias → accession" steps of the sample-name chain merge
   into a single `node.Id.Value` lookup — the ArcId already encodes the priority.

## Shared infrastructure — `Export/Readiness.fs`

Compiled after `Ingest/Ingest.fs` in the ArcIR fsproj. Everything below is `internal` except the public
`Export` facade module.

### `ExportTree` — the in-memory artifact

```fsharp
type ExportEntry =
    | TextFile of relPath: string * content: string
    | BinaryFile of relPath: string * bytes: byte[]      // e.g. a paper PDF copied verbatim
    | CopyFrom of relPath: string * sourcePath: string    // large files streamed, not buffered

type ExportTree = { Root: string; Entries: ExportEntry list }   // Root = the top folder name
```

`Export.writeTo (baseDir: string) (tree: ExportTree)` creates `baseDir/tree.Root/…` and writes each entry
(text as UTF-8 no-BOM, matching `Html.writeFile`). Pure builders return `ExportTree`; tests assert on
`Entries` without disk I/O.

### `CountMatrixSource` — the raw-bytes resolver

```fsharp
/// Resolves a count-file Resource node (keyed `count:<Filename>`) back to its raw rows.
type CountMatrixSource = { openRows: ResourceFile -> seq<string> }   // header + data lines, in order
```

Constructors mirror the ingest readers: `Export.countSourceFromFolder path`,
`Export.countSourceFromArchive path`. The resolver matches on the `Filename` property already stored on the
`CountMatrix` node, so the IR and the raw source stay in sync by filename.

This satisfies the "pass count path(s) to F2" decision: the caller constructs the source from a path, and
the export function takes it alongside the IR.

### `ProjectView` — the resolved per-project sub-graph

```fsharp
type ProjectView =
    { Project: ArcObject
      Studies: ArcObject list
      Samples: ArcObject list
      Experiments: ArcObject list
      Runs: ArcObject list
      CountFiles: (ArcObject * ArcObject list) list   // file node * its column nodes
      Papers: ArcObject list }
```

`Readiness.projectView (ir: ArcIR) (projectId: ArcId) : ProjectView` walks the graph from the project. The
run/experiment/sample spine is edge-reachable (`hasStudy`, `hasSample`, `hasExperiment`, `producesData`,
`hasColumn`). **Project→run reachability is the one gap** (constraint 3): for the baseline, `projectView`
takes *all* count files in the IR (single-project assumption); the multi-project resolver is deferred to the
Enabling change.

### Name helpers (annotation-aware)

```fsharp
/// Look up an annotation by its property term Name (e.g. "Title", "Description", "sample_name").
val Readiness.annotationText : name: string -> o: ArcObject -> string option

/// Filesystem-safe project folder name. Slugified BioProject <TITLE> only.
/// Never falls back to <NAME> or accession. Errors if no Title annotation exists.
val Readiness.projectSlug : p: ArcObject -> string

/// Resolve a sample name for a run accession via the graph join:
/// run -> hasExperiment -> experiment -> hasSample -> biosample.
/// Precedence: "sample_name" annotation -> "Title" annotation -> node ArcId (accession/alias).
val Readiness.sampleName : view: ProjectView -> runAccession: string -> string option

/// The header-rename policy: all-or-nothing sample names, else S_0..S_N.
val Readiness.headerLabels : view: ProjectView -> columns: CountColumn list -> Map<int, string>
```

#### `projectSlug` — implementation (per locked decision)

1. `annotationText "Title" p` — the BioProject `<TITLE>` annotation (emitted by `BioProjectConversion.convert` via `Annotations.stringField source "Title" project.Title`).
2. Slugify: lowercase, non-`[a-z0-9]` → `_`, collapse consecutive `_`, trim leading/trailing `_`, truncate to 60 chars.
3. **No fallback** — if no `Title` annotation, raise (the user said "never fall back on accession").
4. Never reads `Name` (the user said `<NAME>` "contains bad input").

#### `sampleName` — implementation (per locked decision)

The join, step by step:

1. Find the Run node by `ArcId.Create runAccession` in the IR's `Objects` map.
2. `ArcIR.outgoing runId ir` → find the `Rel.hasExperiment` edge → its `Object` is the experiment `ArcId`.
3. Look up the experiment node; `ArcIR.outgoing expId ir` → find the `Rel.hasSample` edge → its `Object` is
   the biosample `ArcId`. (The `pendingSampleRef` resolver already prefers the BioSample external id, so this
   edge lands on `SAMD00064197`, not the SRA `DRS039895`.)
4. On the BioSample node, try in order:
   - `annotationText "sample_name"` — the `<SAMPLE_ATTRIBUTE><TAG>sample_name</TAG>` attribute annotation
     (emitted by `Annotations.attributeAnnotations`; term Name = the `Tag` value, here `"sample_name"`).
   - `annotationText "Title"` — the `<TITLE>` element annotation (emitted by `BioSampleConversion.convert`
     via `Annotations.stringField source "Title" sample.Title`).
   - `Some node.Id.Value` — the `ArcId` (already encodes accession-then-alias priority via `Convert.entityId`).
5. Return `None` if the Run node isn't found (unmapped column, e.g. `DRR072835` in the fixture).

**Note on "alias → accession":** the IR does not store `alias` as a separate annotation. `Convert.entityId`
keys the node by accession, falling back to alias when accession is absent. So "alias → accession" collapses
to `node.Id.Value` — the ArcId already encodes the priority. This is a minor deviation from the user's 4-step
chain (steps 3 and 4 merge into one), documented here for implementation.

#### `headerLabels` — implementation (per locked decision)

The all-or-nothing policy:

1. For each run-accession column in the count file, compute `sampleName view col.RunAccession` → `string option`.
2. Collect all candidates as a list of `option<string>` (one per run column, in file order).
3. **All-or-nothing check:** if every candidate is `Some` *and* the set of candidate strings is fully unique
   across the file → use the sample names.
4. Otherwise → `S_0, S_1, …, S_N` for **all** run columns (0-based, matching the user's `S_0 … S_N` notation).
5. The feature column (index 1, `gene_id`) is preserved verbatim and not counted in `S_k` indexing.

This matches the user's phrasing "if unique, otherwise S_0…S_N" and avoids a mixed header where some columns
are named and some are `S_k`.

## R0 — Plain data (the baseline)

### Goal

The lowest readiness format: the count matrix, in a folder named for the project, with sample-labelled column headers,
and **nothing else** — no README, no provenance, no hint of INSDC. It answers "can a model do anything with
just the numbers and the barest labels?"

### Output shape

```
<projectSlug>/
  <projectSlug>.tsv          # the count matrix, header row rewritten
```

One file per source count matrix (usually one). If a project has multiple matrices, suffix with the file
stem: `<projectSlug>__<stem>.tsv`.

### Header-rename policy (`headerLabels`)

The feature column (index 1, `gene_id`) is preserved verbatim. For the run-accession columns:

1. Resolve each column's `RunAccession` to a `sampleName` via the join.
2. Compute the candidate labels for **all** run columns in the file.
3. **All-or-nothing:** if every candidate is `Some` *and* the set is fully unique across the file, use the
   sample names. Otherwise fall back to positional `S_0 … S_N` for every run column. (This matches "if
   unique, otherwise S_0…S_N" and avoids a mixed header where some columns are named and some are `S_k`.)

`S_k` indexing is 0-based over the run columns in header order; the feature column is not counted.

### Algorithm

```
r0 ir source projectId:
  view    = projectView ir projectId
  slug    = projectSlug view.Project
  entries =
    for (fileNode, columns) in view.CountFiles:
      rows    = source.openRows (fileNode -> ResourceFile)
      labels  = headerLabels view (columns as CountColumn list)   // Map<colIndex,newName>
      header' = rewrite the delimiter-split header cells by 1-based index using labels
      body    = rows |> Seq.tail                                   // untouched
      TextFile("<name>.tsv", header' + newline + body joined)
  { Root = slug; Entries = entries }
```

Delimiter detection reuses the ingest rule (tab if the header contains one, else comma). Column fragments
already carry the 1-based `Index`, so the rewrite is positional and never re-parses semantics.

### Public surface

```fsharp
[<RequireQualifiedAccess>]
module Export =
    /// R0 for one project in the IR.
    val r0        : ArcIR -> CountMatrixSource -> projectId: ArcId -> ExportTree
    /// R0 for every BioProject node in the IR (one tree each).
    val r0All     : ArcIR -> CountMatrixSource -> ExportTree list
    val writeTo   : baseDir: string -> ExportTree -> unit
    val countSourceFromFolder  : string -> CountMatrixSource
    val countSourceFromArchive : string -> CountMatrixSource
```

### Files & build

- New folder `src/BioFSharp.INSDC.ArcIR/Export/`, files `Readiness.fs` then `R0.fs`, added to
  `BioFSharp.INSDC.ArcIR.fsproj` **after** `Ingest/Ingest.fs`.
- No new dependencies (zip/stream via in-box `System.IO.Compression`, already used by the readers).

### Tests (regression, per repo policy)

In `tests/BioFSharp.INSDC.Tests/Tests.fs`, building on the same fixtures the ingest playground uses
(`PRJDB5192` + `counts-PRJDB5192.tsv`, whose header is `gene_id  DRR072834  DRR072835`):

**IR construction** (same pattern as `GraphMlExportTests`):

```fsharp
let read reader file = reader (TestFiles.fixture file) |> Seq.exactlyOne
let ir =
    [ INSDC.bioProject (read BioProject.read "PRJDB5192.xml")
      INSDC.study (read Study.read "DRP003416.xml")
      INSDC.bioSample (read BioSample.read "SAMD00064197.xml")
      INSDC.experiment (read Experiment.read "DRX066772.xml")
      INSDC.run (read Run.read "DRR072834.xml")
      Ingest.countData (IngestReaders.readCountFile (TestFiles.fixture "counts-PRJDB5192.tsv"))
    ]
    |> INSDC.build
```

**Test cases:**

1. **Folder name** — `ExportTree.Root` = the project slug. Assert exact slug derived from `PRJDB5192`'s
   `<TITLE>` annotation ("The gene-body chromatin modifications dynamics mediates epigenome differentiation
   in Arabidopsis" → slugified + truncated).

2. **Header, positional fallback** — the count fixture has `DRR072834` (in IR, resolves to `sample_name`
   "WT") and `DRR072835` (not in IR, unmapped). Because not all columns resolve, the policy falls back to
   `S_0 S_1`; assert the full fallback header line: `gene_id\tS_0\tS_1`.

3. **Body untouched** — data rows (`AT1G01010\t105\t98`, `AT1G01020\t12\t15`, `AT1G01030\t0\t3`) are
   byte-identical to the source.

4. **Uniqueness fallback** — a crafted two-column IR whose samples share a name falls back to `S_*`.
   Assert header.

5. **Named path (all-mapped, all-unique)** — craft a count file / IR where every run column resolves to a
   unique sample name → header uses the sample names. Assert the exact header. (Requires a small crafted
   fixture or inline TSV content.)

6. **`writeTo` round-trip** — `Export.r0 … |> Export.writeTo tmpDir` then read back from disk; assert
   content matches the in-memory `build` output.

These lock the header-rename policy the way the repo's regression-test policy requires for generation
changes.

### Playground

`playground/export_r0.fsx`: build the fixture IR (as `playground/ingest.fsx` does), then
`Export.r0 ir (Export.countSourceFromFolder …) (ArcId.Create "PRJDB5192") |> Export.writeTo "./out"` and
print the tree.

## R1 — Un/weakly structured (superseded; implemented as a crawl)

> **Superseded 2026-07-13.** The sketch below is kept for history only. R1 was redefined and built: it is
> now the **DEE2 count archive verbatim + the paper**, with **R1A = JATS XML and R1B = PDF** — note this
> **inverts** the old A=pdf / B=jats assignment — and **R1C is dropped** (shipping the archive verbatim
> already retains the run-accession headers that were R1C's only distinguishing feature). There is no
> `description.txt` and no re-headered count matrix, so R1 needs neither `titleAbbrev` nor `PaperSource`
> nor any IR: it is materialized entirely by the crawler. See
> [`plans/claude/r1-crawlers.md`](../claude/r1-crawlers.md) and the canonical
> [`plans/claude/arcir-export-readiness.md`](../claude/arcir-export-readiness.md).

*Historical sketch:*

Same folder, but *decontextualized*: strip the obvious INSDC surface so the record isn't trivially
re-identifiable, and add human context.

```text
<slug>/
  description.txt            # BioProject Description annotation, verbatim prose
  <title-abbrev>.tsv        # counts; filename = abbreviation of the project Title (not the accession)
  paper.pdf | paper.jats.xml
```

- **R1A/R1B** — headers are sample names / `S_k` as in R0 (A = paper.pdf, B = paper JATS XML).
- **R1C** — identical, but headers keep the **run accessions** verbatim (the one bit of INSDC identity R1
  deliberately retains).
- Reuses `annotationText "Description"`, a new `titleAbbrev`, and the `Publication` node + its
  `CopyFrom`/`BinaryFile` source (the paper file, like counts, is referenced by the IR but its bytes come
  from a caller source — add `PaperSource` mirroring `CountMatrixSource`).

## R2 — Structured standard (sketch)

Emit the **INSDC XML records** in a canonical folder layout, plus counts (and, for R2B, the paper).

```
<slug>/
  bioproject.xml
  studies/ DRP003416.xml …
  samples/ SAMD00064197.xml …
  experiments/ DRX066772.xml …
  runs/ DRR072834.xml …
  counts/ <matrix>.tsv       # full DEE2 metadata retained (header = run accessions)
  paper.(xml|pdf)            # R2B
```

- Needs the **original XML** per node. The IR doesn't hold source XML either; either (a) add a
  `RecordSource` resolver (accession → XML path), or (b) round-trip from the typed records via
  `BioFSharp.IO.INSDC` writers if/when they exist. Decide before detailing R2.
- This is where the **BioProject↔Study edge** (Enabling change) pays off, to place records under the right
  project in a multi-project IR.

## R3 — Ontology-backed (sketch)

The ArcIR graph *is* R3 — it's already ontology-annotated. Emit the graph in a durable, self-describing
form. Candidates (decide later): the existing GraphML, an RDF/Turtle serialization of the
objects+relations+annotations (closest to the "ontology-backed standard" claim), or a JSON-LD framing.
No new joins needed; this is a serializer over the whole graph, not a per-project materializer — so it may
live beside `GraphMl`/`Html` rather than under `Export/`. Left open per the user's note ("not ready to think
about if there should be a data format for it").

## R4 — FAIR digital object (sketch)

An **ARC in the new non-ISA YAML form**.

- **R4A** — Investigation (administrative metadata from BioProject/Study/contacts) + data provenance as
  assays over the DEE2 counts, **no datamap**.
- **R4B** — adds a datamap for the counts, plus **sample provenance** as assays (curated by hand now,
  LLM-assisted first pass later for scaling).
- Depends on the target YAML schema (the new ARC format), which isn't pinned here. This readiness format is specified
  once that schema is available; the ArcIR→ARC mapping doc (`plans/arcir-mapping.md`) already establishes
  the Kind→ISA correspondence to build on.

## Enabling change (shared, do before R2/R4 multi-project)

Add a first-class `BioProject --hasStudy--> Study` (or `Study --hasProject--> BioProject`) edge in the
mapping, resolved via the Study `ProjectId` ↔ BioProject numeric identifier. This makes `projectView`'s
project→run→count reachability a pure edge-walk and removes R0's single-project assumption. Ships with a
regression test per the ontology-change policy. **Not required for the R0 baseline.**

## Sequencing

1. **R0** — `Export/Readiness.fs` (ExportTree, CountMatrixSource, ProjectView single-project, name helpers)
   + `Export/R0.fs` + tests + playground. Self-contained; no enabling change needed.
2. ~~**R1**~~ — **done, and it left the export layer entirely**: R1 is two raw artifacts (the DEE2 archive
   + the paper), crawler-materialized, needing no `PaperSource`, no `titleAbbrev` and no IR. See
   [`plans/claude/r1-crawlers.md`](../claude/r1-crawlers.md).
3. **Enabling change** — BioProject↔Study edge (unblocks correct multi-project scoping).
4. ~~**R2**~~ — **done, likewise crawler-materialized.** See
   [`plans/claude/r2-crawlers.md`](../claude/r2-crawlers.md).
5. **R3 / R4** — after their formats (RDF/JSON-LD; the new ARC YAML) are pinned.
