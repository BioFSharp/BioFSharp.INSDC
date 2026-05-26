# Plan: BioFSharp.INSDC.SQLite — Schema + Deconstruction/Reconstruction Layer

## Context

The next phase of `BioFSharp.INSDC` is a SQLite-backed store for the INSDC entity hierarchy (`BioProject > Study > BioSample > Experiment > Run`), intended to feed a crawler for these metadata records.

A prior iteration left behind a SQL dump at [insdc_schema.sql](src/BioFSharp.INSDC.SQLite/schema/insdc_schema.sql) (~80 tables) — that is the only file currently in the new [BioFSharp.INSDC.SQLite/](src/BioFSharp.INSDC.SQLite/) project. Meanwhile, the F# side has moved on: types are now auto-generated from XSDs via `dotnet xscgen` into [BioFSharp.FileFormats.INSDC/Generated/](src/BioFSharp.FileFormats.INSDC/Generated/), with thin F# I/O wrappers in [BioFSharp.IO.INSDC/](src/BioFSharp.IO.INSDC/) for XML round-tripping.

Two things need to happen:

1. Reconcile the schema with the freshly generated types — fix obvious bugs in the dump, and decide table-by-table whether to keep, rework, or drop.
2. Build an F# layer that **deconstructs** an INSDC record into the many flat SQLite rows that represent it, and **reconstructs** it on the way back out.

---

## Reference — side-by-side: existing SQLite schema vs generated F# types

### Root entities

| F# type (Object-derived) | SQLite core table | Match notes |
|---|---|---|
| `BioProject` ([BioProject.cs:22](src/BioFSharp.FileFormats.INSDC/Generated/BioFSharp.FileFormats.INSDC/BioProject.cs#L22)) | `project` ([insdc_schema.sql:21](src/BioFSharp.INSDC.SQLite/schema/insdc_schema.sql#L21)) | Core columns line up (accession, alias, broker_name, center_name, title, name, description, first_public). Snake_case consistent here. |
| `Study` ([Study.cs:29](src/BioFSharp.FileFormats.INSDC/Generated/BioFSharp.FileFormats.INSDC/Study.cs#L29)) | `study` + `study_descriptor` + `study_type` | F# `Study` is thin; almost everything sits under `Descriptor`. SQLite splits the descriptor into its own table — sane. `study_type` is two columns (existing / new) and could be inlined. |
| `BioSample` ([BioSample.cs:29](src/BioFSharp.FileFormats.INSDC/Generated/BioFSharp.FileFormats.INSDC/BioSample.cs#L29)) | `sample` + `sample_name` | OK shape. Column casing inconsistent (`brokerName`/`centerName` vs `broker_name`/`center_name` elsewhere). `sample_name.displayName Text` has a typo (`Text` not `TEXT`). |
| `Experiment` ([Experiment.cs:30](src/BioFSharp.FileFormats.INSDC/Generated/BioFSharp.FileFormats.INSDC/Experiment.cs#L30)) | `experiment` + ~24 detail tables | Largest entity. `Design`/`Library`/`LibraryDescriptor`/`SpotDescriptor`/`Platform`/`Processing` map roughly. Platform discriminated union is **not** captured — only `experiment_platform.instrumentModel` exists; per-platform parameters are lost. |
| `Run` ([Run.cs:26](src/BioFSharp.FileFormats.INSDC/Generated/BioFSharp.FileFormats.INSDC/Run.cs#L26)) | `run` + ~19 detail tables | Same shape as experiment. Same platform-DU gap. Schema has two syntax errors here (see "Bugs" below). |

### Common building blocks

| F# type | SQLite shape | Match notes |
|---|---|---|
| `Object` base (Identifiers, Alias, CenterName, BrokerName, Accession) — [Object.cs:34](src/BioFSharp.FileFormats.INSDC/Generated/BioFSharp.FileFormats.INSDC/Object.cs#L34) | Each entity's core table repeats the columns | Fine. No need to share a parent table. |
| `RefObject` base (Refname, Refcenter, Accession, Identifiers) — [RefObject.cs:34](src/BioFSharp.FileFormats.INSDC/Generated/BioFSharp.FileFormats.INSDC/RefObject.cs#L34) | `experiment_studyRef`, `experiment_sampleDescriptor`, `run_experimentRef` each get their own table **plus** a parallel set of nested ID tables | Big duplication. Each reference re-creates the entire 5-table ID family (`*RefPrimaryID`, `*RefSecondaryID`, `*RefExternalID`, `*RefSubmitterID`, `*RefUUID`). |
| `Identifier` composite ([Identifier.cs:25](src/BioFSharp.FileFormats.INSDC/Generated/BioFSharp.FileFormats.INSDC/Identifier.cs#L25)) — PrimaryId, SecondaryId[], ExternalId[], SubmitterId, Uuid[] | 5 separate tables per owner | Same shape repeated for every owner and reference. Inconsistent column orderings between siblings. |
| `Attribute` (Tag, Value, Units) — [Attribute.cs:25](src/BioFSharp.FileFormats.INSDC/Generated/BioFSharp.FileFormats.INSDC/Attribute.cs#L25) | `{entity}_attributes (tag, value, units)` × 5 entities | Clean and consistent. Keep. |
| `Link` (DU: UrlLink \| XrefLink \| EntrezLink) — [Link.cs:26](src/BioFSharp.FileFormats.INSDC/Generated/BioFSharp.FileFormats.INSDC/Link.cs#L26) | `{entity}_links (db, id, label, url, item, itemElementName)` × 5 | `item` + `itemElementName` are the DU discriminator. Works but opaque. |
| `Platform` (DU over 11 sequencing techs, each with own config) — [Platform.cs:27](src/BioFSharp.FileFormats.INSDC/Generated/BioFSharp.FileFormats.INSDC/Platform.cs#L27) | `experiment_platform.instrumentModel`, `run_platform.platformName` — single TEXT field | **Gap**: only the instrument string is stored. Platform-specific parameters are dropped. |
| `BioSampleName` (TaxonId, ScientificName, CommonName, DisplayName) | `sample_name` | Matches, modulo the `Text` typo. |
| `Library` / `LibraryDescriptor` / `SpotDescriptor` / `Processing` | `experiment_design`, `experiment_spotDescriptor`, `experiment_processingDirectives`, `experiment_pipeline`, `experiment_targetedLoci` (and `run_*` analogues) | Coverage exists but denormalized; e.g. `LibraryStrategy/Source/Selection` enums live as free TEXT. |

### Hierarchy / cross-entity references

- F# uses **typed reference objects**: `Experiment.StudyRef`, `Run.ExperimentRef`, `Library.SampleDescriptor`. Each carries full `Refname/Refcenter/Accession/Identifiers`.
- SQLite has **two parallel mechanisms** that aren't reconciled:
  1. `accession_map (project_accession, study_accession, sample_accession, experiment_accession, run_accession)` — one row flattens the whole tree.
  2. `experiment_studyRef`, `experiment_sampleDescriptor`, `run_experimentRef` (+ their nested ID tables) — per-reference detail.
- Neither side enforces FKs to the target entity. `accession_map` has no PK.

### Stuff in the F# types with no SQLite home

- `BioProject.SubmissionProject` and `BioProject.UmbrellaProject` (nested project compositions).
- Platform-DU per-platform parameters.
- `Run.RunProperty` (RunRunType) and full `Run.Processing` (only partial coverage today).
- `LibraryDescriptor.LibraryLayout` single vs paired-end distinction.

### Stuff in the SQLite schema with no clean F# counterpart

- `project_collaborators` — F# `BioProject.Collaborators` is `Collection<string>` directly on the project; side-table is correct but column is one untyped TEXT.
- `run_inner (assembly, sequence)` — unclear purpose; possibly leftover.
- `accession_map` — convenience denormalization; redundant once proper FKs exist.

---

## Reference — what's wrong with the existing schema

Grouped by severity. The dump has ~80 tables; problems cluster into five categories.

### A. Outright bugs (file does not cleanly create itself)

- **A1 — Missing column type.** [insdc_schema.sql:34](src/BioFSharp.INSDC.SQLite/schema/insdc_schema.sql#L34) — `run_dataBlockType.member_name` has no type declaration.
- **A2 — Invalid SQL type.** [insdc_schema.sql:43](src/BioFSharp.INSDC.SQLite/schema/insdc_schema.sql#L43) — `run_experimentRef.submitterIDNamespace LaTeXString` — `LaTeXString` isn't a SQLite type; looks like a botched search-and-replace.
- **A3 — Stray column name.** [insdc_schema.sql:76](src/BioFSharp.INSDC.SQLite/schema/insdc_schema.sql#L76) — `accessionStudy` doesn't fit surrounding naming.

### B. Inconsistencies that work today but cause endless friction

- **B1 — Mixed casing on duplicated columns.** `project.broker_name` / `sample.brokerName`; `project.center_name` / `sample.centerName` / `experiment.centerName`.
- **B2 — Field-order inconsistencies.** `sample_secondaryID` is `(label, value)` while `project_secondaryID` is `(value, label)`.
- **B3 — Same concept, different names.** `run_secID` vs `experiment_secondaryID` vs `sample_secondaryID`. `run_experimentRefExID` vs `experiment_studyRefExternalID`.
- **B4 — Inconsistent FK actions.** Most tables `ON UPDATE CASCADE`; `study_links` and `experiment_design` use `ON UPDATE NO ACTION`; many specify no `ON DELETE`. Reads like accidents.
- **B5 — `Text` vs `TEXT`** at [insdc_schema.sql:16](src/BioFSharp.INSDC.SQLite/schema/insdc_schema.sql#L16). Harmless to SQLite but a smell.

### C. Structural problems (the schema's shape works against you)

- **C1 — Identifier fragmentation.** Five sibling tables (`*_primaryID`, `*_secondaryID`, `*_externalID`, `*_submitterID`, `*_uuid`) per entity. F# uses one `Identifier` composite.
- **C2 — Reference fragmentation, squared.** Every typed reference re-creates the entire 5-table identifier family (`*RefPrimaryID`, etc.) — ~15 extra tables for three references.
- **C3 — Two competing hierarchy mechanisms.** `accession_map` vs per-edge `*_studyRef`/`*_experimentRef`. No source of truth.
- **C4 — No cross-entity FKs.** Reference fields are plain TEXT — an experiment can point at a nonexistent study with no complaint.
- **C5 — `accession_map` has no primary key.** Cardinality undefined.
- **C6 — Sparse tables that should be columns.** `experiment_platform`, `run_inner`, `run_processingDirectives`, `experiment_processingDirectives` are 1–2-column tables only because they're optional.

### D. Things the F# types model that the schema loses

- **D1 — Platform variants.** F# `Platform` is a DU over 11 techs each with config; schema stores only `instrumentModel`/`platformName`.
- **D2 — Nested project compositions.** `BioProject.SubmissionProject`, `BioProject.UmbrellaProject` have no SQLite home.
- **D3 — Library layout single vs paired-end.**
- **D4 — `Run.RunProperty` and full `Run.Processing`.**

### E. Performance and integrity gaps

- **E1 — Zero indexes** (even on owner-FK columns).
- **E2 — No views, no triggers.**
- **E3 — Detail tables have no PKs** — duplicates accepted freely.

---

## Decisions (confirmed with user)

1. **Schema strategy — Hybrid.** Keep what works (per-entity core tables, the `*_attributes` pattern, the descriptor / sample_name / spot_descriptor splits). Rebuild identifiers, references/hierarchy, platform. Fix all A/B bugs along the way. The existing dump becomes reference, not constraint.
2. **F# ↔ SQLite — `Microsoft.Data.Sqlite` + hand-written SQL.** No ORM. Connection helpers and parameter binding live in `Internal/`.
3. **Identifier modeling — one identifier table per owner, kind-discriminated.**
4. **Scope this phase — schema + deconstruction/reconstruction only.** No crawler entry points yet.
5. **Table naming — match F# type names: `bioproject`, `study`, `biosample`, `experiment`, `run`** (avoids `BioProject` vs `Project` and `BioSample` vs `Sample` ambiguity).

---

## Reference — proposed schema shape (hybrid)

Single SQL file at [src/BioFSharp.INSDC.SQLite/schema/insdc_schema.sql](src/BioFSharp.INSDC.SQLite/schema/insdc_schema.sql), rewritten in place. Conventions:

- All identifiers `snake_case`. All types uppercase (`TEXT`, `INTEGER`).
- Every table has an explicit PRIMARY KEY.
- Every FK uses `ON DELETE CASCADE ON UPDATE CASCADE` unless documented otherwise.
- Indexes on every FK column.
- `PRAGMA foreign_keys = ON` documented at top of schema; enforced by the F# connection helper on every open.
- Entity table names: `bioproject`, `study`, `biosample`, `experiment`, `run`.

### Core entities + parent FKs

PK on `accession` for each. Parent FKs live on the entity itself, replacing `accession_map`:

- `study.bioproject_accession` → `bioproject.accession` (NULLable)
- `biosample` has no parent FK
- `experiment.study_accession` → `study.accession` (NOT NULL)
- `run.experiment_accession` → `experiment.accession` (NOT NULL)

### Nested composite tables (kept, cleaned up)

- `study_descriptor (study_accession PK/FK, study_title, study_type, study_abstract, center_project_name, study_description, ...)` — collapses old `study_descriptor` + `study_type`.
- `biosample_name (biosample_accession PK/FK, taxon_id, scientific_name, common_name, display_name)` — fixes the `Text` typo.
- `experiment_design (experiment_accession PK/FK, design_description, library_strategy, library_source, library_selection, library_layout_kind, library_layout_nominal_length, library_layout_nominal_sdev)` — `library_layout_kind` captures D3.
- `experiment_spot_descriptor` / `run_spot_descriptor` — PK on `(owner_accession, read_index)`.
- `experiment_targeted_loci` — PK on `(experiment_accession, locus_name)`.
- `run_data_block (run_accession PK/FK, files, member_name TEXT, ...)` — fixes A1.

### Unified identifier tables

One per owner. Replaces ~25 entity tables + ~15 reference-nested ID tables.

```sql
CREATE TABLE bioproject_identifiers (
    bioproject_accession TEXT NOT NULL REFERENCES bioproject(accession) ON DELETE CASCADE ON UPDATE CASCADE,
    kind                 TEXT NOT NULL CHECK (kind IN ('PRIMARY', 'SECONDARY', 'EXTERNAL', 'SUBMITTER', 'UUID')),
    ordinal              INTEGER NOT NULL DEFAULT 0,
    value                TEXT NOT NULL,
    label                TEXT,
    namespace            TEXT,
    PRIMARY KEY (bioproject_accession, kind, ordinal)
);
CREATE INDEX ix_bioproject_identifiers_kind ON bioproject_identifiers (bioproject_accession, kind);
```

Analogous: `study_identifiers`, `biosample_identifiers`, `experiment_identifiers`, `run_identifiers`.

### Reference tables

Each typed reference gets one row in a reference table plus rows in a sibling identifier table:

```sql
CREATE TABLE experiment_study_ref (
    experiment_accession TEXT PRIMARY KEY REFERENCES experiment(accession) ON DELETE CASCADE ON UPDATE CASCADE,
    accession            TEXT REFERENCES study(accession) ON UPDATE CASCADE, -- soft FK; NULL ok when only refname given
    refname              TEXT,
    refcenter            TEXT
);
CREATE TABLE experiment_study_ref_identifiers (
    experiment_accession TEXT NOT NULL REFERENCES experiment_study_ref(experiment_accession) ON DELETE CASCADE ON UPDATE CASCADE,
    kind                 TEXT NOT NULL CHECK (kind IN ('PRIMARY', 'SECONDARY', 'EXTERNAL', 'SUBMITTER', 'UUID')),
    ordinal              INTEGER NOT NULL DEFAULT 0,
    value                TEXT NOT NULL,
    label                TEXT,
    namespace            TEXT,
    PRIMARY KEY (experiment_accession, kind, ordinal)
);
```

Same shape for `experiment_sample_descriptor` / `experiment_sample_descriptor_identifiers` and `run_experiment_ref` / `run_experiment_ref_identifiers`.

### Attribute and link tables

- `{entity}_attributes (entity_accession FK, ordinal INTEGER, tag TEXT NOT NULL, value TEXT, units TEXT, PRIMARY KEY(entity_accession, ordinal))` — five tables (`bioproject_attributes`, etc.).
- `{entity}_links (entity_accession FK, ordinal INTEGER, link_kind TEXT CHECK ('URL'|'XREF'|'ENTREZ'), label TEXT, url TEXT, db TEXT, id TEXT, query TEXT, PRIMARY KEY(entity_accession, ordinal))` — replaces opaque `item`/`itemElementName` with explicit `link_kind`.

### Platform (resolves D1)

```sql
CREATE TABLE experiment_platform (
    experiment_accession TEXT PRIMARY KEY REFERENCES experiment(accession) ON DELETE CASCADE ON UPDATE CASCADE,
    kind                 TEXT NOT NULL CHECK (kind IN ('LS454','ILLUMINA','HELICOS','ABI_SOLID','COMPLETE_GENOMICS','BGISEQ','OXFORD_NANOPORE','PACBIO_SMRT','CAPILLARY','DNBSEQ','AVITI')),
    instrument_model     TEXT
);
CREATE TABLE experiment_platform_params (
    experiment_accession TEXT NOT NULL REFERENCES experiment_platform(experiment_accession) ON DELETE CASCADE ON UPDATE CASCADE,
    key                  TEXT NOT NULL,
    value                TEXT,
    PRIMARY KEY (experiment_accession, key)
);
```

Same shape for `run_platform` / `run_platform_params`. Per-platform fields go in the key/value bag — avoids 11 sub-tables; deconstruction layer serializes per-DU-case fields into params.

### Dropped from existing schema

- `accession_map` — replaced by parent FKs.
- `run_inner` — unclear purpose, no F# counterpart.
- Fragmented `*_primaryID`/`*_secondaryID`/`*_externalID`/`*_submitterID`/`*_uuid` tables (per entity and per reference) — replaced by unified `*_identifiers` tables.

### Deferred (out of scope this phase; TODO comment in schema)

- `BioProject.SubmissionProject` and `BioProject.UmbrellaProject` (D2).
- Full `Run.Processing` / `Run.RunProperty` (D4) — minimal coverage retained.

---

## Reference — F# layer shape

New project [src/BioFSharp.INSDC.SQLite/BioFSharp.INSDC.SQLite.fsproj](src/BioFSharp.INSDC.SQLite/BioFSharp.INSDC.SQLite.fsproj), `netstandard2.0`, mirroring [BioFSharp.IO.INSDC.fsproj](src/BioFSharp.IO.INSDC/BioFSharp.IO.INSDC.fsproj).

### Module layout

```
src/BioFSharp.INSDC.SQLite/
├── BioFSharp.INSDC.SQLite.fsproj
├── schema/
│   └── insdc_schema.sql                      (rewritten, embedded resource)
├── Internal/
│   ├── Sql.fs                                connection + transaction helpers; PRAGMA foreign_keys=ON
│   ├── Schema.fs                             loads embedded schema, applies to a fresh DB
│   ├── Identifiers.fs                        writeIdentifiers / readIdentifiers — generic over owner-table name
│   ├── Attributes.fs                         same pattern for Attribute collections
│   ├── Links.fs                              same pattern, with link_kind DU handling
│   └── References.fs                         writeRefObject / readRefObject — covers StudyRef, SampleDescriptor, ExperimentRef
├── Schema.fs                                 public: Schema.init (conn: SqliteConnection) → unit
├── BioProject.fs                             public: insert, tryGet, delete, listAccessions
├── Study.fs
├── BioSample.fs
├── Experiment.fs
└── Run.fs
```

### Public per-entity API (mirrors `BioFSharp.IO.INSDC` style)

```fsharp
module BioProject =
    /// Deconstruct a BioProject into rows across bioproject, bioproject_identifiers,
    /// bioproject_links, bioproject_attributes, bioproject_collaborators
    /// and insert in a single transaction. Throws on FK or constraint violations.
    val insert : SqliteConnection -> BioProject -> unit

    /// Reconstruct a BioProject by joining all owner tables for the given accession.
    val tryGet : SqliteConnection -> accession:string -> BioProject option

    val delete : SqliteConnection -> accession:string -> unit

    /// List all accessions present in the bioproject table (for crawler bookkeeping).
    val listAccessions : SqliteConnection -> string seq
```

### Deconstruction strategy (BioProject example)

Per-entity transaction:

1. INSERT into `bioproject`.
2. For each `(kind, items)` in `project.Identifiers` → INSERT into `bioproject_identifiers` with `kind` and `ordinal`.
3. For each item in `project.ProjectAttributes` → INSERT into `bioproject_attributes`.
4. For each item in `project.ProjectLinks` → resolve DU case → INSERT into `bioproject_links` with `link_kind` set.
5. Experiment/Run additionally call `References.writeRefObject` for their outgoing references.

Helper signature in `Internal/Identifiers.fs`:

```fsharp
type IdentifierOwner = { Table: string; AccessionColumn: string; Accession: string }

val writeIdentifiers : SqliteConnection -> IdentifierOwner -> Identifier -> unit
val readIdentifiers  : SqliteConnection -> IdentifierOwner -> Identifier
```

The same helper serves all five entities and all three reference contexts — just with a different `Table`. Payoff of unifying the identifier schema.

### Reconstruction strategy

`tryGet` runs **one query per owner-table family**, then assembles:

1. Single-row SELECT on `bioproject`.
2. SELECT all `bioproject_identifiers`; group by `kind` to rebuild `Identifier` composite.
3. SELECT all `bioproject_attributes`, `bioproject_links`, `bioproject_collaborators`.
4. Experiment/Run: SELECT the reference row + its identifier rows, build `RefObject` values, wire into the parent.
5. Construct the C# type via parameterless constructor; populate Collections via `.Add(...)`.

O(entity_count) queries per reconstruction; with FK indexes this stays cheap.

---

## Execution chunks

### Chunk 1 — Schema rewrite ✅

- [x] Rewrite [src/BioFSharp.INSDC.SQLite/schema/insdc_schema.sql](src/BioFSharp.INSDC.SQLite/schema/insdc_schema.sql) per the "Reference — proposed schema shape" section above
- [x] Top of file: `PRAGMA foreign_keys = ON;` and a banner comment listing conventions
- [x] Core entity tables: `bioproject`, `study` (with `bioproject_accession` FK), `biosample`, `experiment` (with `study_accession` FK), `run` (with `experiment_accession` FK)
- [x] Nested composite tables: `study_descriptor`, `biosample_name`, `experiment_design`, `experiment_spot_descriptor`, `run_spot_descriptor`, `experiment_targeted_loci`, `run_data_block`
- [x] Unified identifier tables × 5: `bioproject_identifiers`, `study_identifiers`, `biosample_identifiers`, `experiment_identifiers`, `run_identifiers`
- [x] Reference tables × 3: `experiment_study_ref` (+_identifiers), `experiment_sample_descriptor` (+_identifiers), `run_experiment_ref` (+_identifiers)
- [x] Attribute tables × 5 (`{entity}_attributes`)
- [x] Link tables × 5 (`{entity}_links`) with `link_kind` CHECK constraint
- [x] Platform tables × 2: `experiment_platform` (+_params), `run_platform` (+_params)
- [x] `bioproject_collaborators` table for `Collection<string>` field
- [x] Indexes on every FK column
- [x] Smoke test: `sqlite3 :memory: < src/BioFSharp.INSDC.SQLite/schema/insdc_schema.sql` exits clean
- [x] FK enforcement verified: inserting an experiment with a missing `study_accession` is rejected with `FOREIGN KEY constraint failed`

**Result:** 42 tables (down from 80 in the dump), 11 indexes. Identifier fragmentation collapsed (5 entity tables × 5 ID kinds → 5 unified identifier tables; 15 reference-nested ID tables → 3 reference identifier tables). `accession_map` and `run_inner` dropped; sparse processing-directive tables inlined into `experiment`/`run`.

### Chunk 2 — SQLite project scaffold ✅

- [x] Create [src/BioFSharp.INSDC.SQLite/BioFSharp.INSDC.SQLite.fsproj](src/BioFSharp.INSDC.SQLite/BioFSharp.INSDC.SQLite.fsproj) — `netstandard2.0`, matches existing `BioFSharp.IO.INSDC.fsproj` style
- [x] Add `PackageReference Include="Microsoft.Data.Sqlite"` (8.0.10 — last line that still targets netstandard2.0; 9.x dropped it)
- [x] Add `ProjectReference` to `BioFSharp.FileFormats.INSDC.csproj` and `BioFSharp.IO.INSDC.fsproj`
- [x] Add `<EmbeddedResource Include="schema/insdc_schema.sql" />`
- [x] Add `<PackageProjectUrl>`, `<Description>`, `<PackageTags>` etc. matching repo conventions
- [x] Register the new project in `BioFSharp.INSDC.slnx`
- [x] `dotnet build src/BioFSharp.INSDC.SQLite` succeeds (empty project compiles)

**Result:** Empty F# library compiles; full solution build is clean. Pre-existing NU1903 warnings on FAKE build tooling are unchanged.

### Chunk 3 — Internal helpers ✅

- [x] `Internal/Sql.fs` — `openConnection : string -> SqliteConnection` (issues `PRAGMA foreign_keys = ON` after open); `withTransaction` wrapper; small param-binding helpers (`execNonQuery`, `queryAll`, `tryQueryOne`, `readStringOrNull`)
- [x] `Internal/Schema.fs` — loads `insdc_schema.sql` from embedded resources and executes against a connection
- [x] `Internal/Identifiers.fs` — `IdentifierOwner` record + `write` / `read` (generic over owner table, accession column, accession value)
- [x] `Internal/Attributes.fs` — `write` / `read` (same shape)
- [x] `Internal/Links.fs` — `write` / `read` with `link_kind` resolution against the `Link` DU (UrlLink / XrefLink / EntrezLink)
- [x] `Internal/References.fs` — `write` / `read` for any `RefObject` subtype, including its sibling identifier table (new()-constrained generic so the right concrete subclass — `ExperimentStudyRef`, `BioSampleDescriptor`, `RunExperimentRef` — is returned)

**Result:** Clean build, zero warnings. Smoke test through `dotnet fsi`: schema loads (42 tables), all 5 identifier kinds round-trip (PRIMARY/SECONDARY/EXTERNAL/SUBMITTER/UUID) with label/namespace preserved, attributes round-trip with NULL value/units preserved, all 3 Link DU cases (URL/XREF/ENTREZ with nullable int64 Id) round-trip cleanly.

### Chunk 4 — Per-entity public modules

- [ ] `Schema.fs` — public `Schema.init : SqliteConnection -> unit`
- [ ] `BioProject.fs` — `insert`, `tryGet`, `delete`, `listAccessions`
- [ ] `Study.fs` — same surface; handles `study_descriptor` denormalization
- [ ] `BioSample.fs` — same surface; handles `biosample_name`
- [ ] `Experiment.fs` — same surface; uses `References` for `StudyRef` and `Library.SampleDescriptor`; serializes `Platform` DU into `experiment_platform` + `_params`
- [ ] `Run.fs` — same surface; uses `References` for `ExperimentRef`; serializes optional `Platform` DU

### Chunk 5 — Tests

- [ ] Add `ProjectReference` to the new SQLite project in [tests/BioFSharp.INSDC.Tests/BioFSharp.INSDC.Tests.fsproj](tests/BioFSharp.INSDC.Tests/BioFSharp.INSDC.Tests.fsproj)
- [ ] Schema init smoke test — open in-memory connection, call `Schema.init`, assert expected table set via `sqlite_master`
- [ ] Round-trip BioProject — fixture XML → `BioFSharp.IO.INSDC.BioProject.read` → `BioProject.insert` → `BioProject.tryGet` → structural compare
- [ ] Round-trip Study
- [ ] Round-trip BioSample
- [ ] Round-trip Experiment (exercises StudyRef, SampleDescriptor, Platform)
- [ ] Round-trip Run (exercises ExperimentRef)
- [ ] FK enforcement test — insert Experiment with bogus `study_accession`; assert SQLite raises FK violation
- [ ] Identifier round-trip — populate all five kinds (Primary, Secondary, External, Submitter, UUID); verify bit-for-bit

### Chunk 6 — End-to-end verification

- [ ] `dotnet build` of the solution succeeds
- [ ] `./build.sh runtests` (or `dotnet test tests/BioFSharp.INSDC.Tests`) — all tests green
- [ ] Append a `RELEASE_NOTES.md` entry for the new `BioFSharp.INSDC.SQLite` package
