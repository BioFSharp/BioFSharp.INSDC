-- BioFSharp.INSDC.SQLite — schema for the INSDC entity hierarchy
--   BioProject > Study > BioSample > Experiment > Run
--
-- Conventions:
--   * All identifiers are snake_case; all SQL types uppercase (TEXT, INTEGER).
--   * Entity tables are named after the F# type: bioproject, study, biosample,
--     experiment, run.
--   * Every table has an explicit PRIMARY KEY.
--   * Foreign keys cascade on both UPDATE and DELETE.
--   * Identifier collections are stored in unified per-owner tables with a
--     kind-discriminator (PRIMARY | SECONDARY | EXTERNAL | SUBMITTER | UUID),
--     mirroring the shape of the F# Identifier composite.
--   * Ordered collections (Attributes, Links, Identifiers, etc.) carry an
--     `ordinal` column so original document order survives a round trip.
--   * Cross-entity references (Experiment.StudyRef, Experiment.SampleDescriptor,
--     Run.ExperimentRef) sit in dedicated reference tables with sibling
--     `*_identifiers` tables for their nested Identifier collections.
--
-- Recurring column meanings (referenced from many tables):
--   accession      INSDC archive-assigned stable identifier for the record
--                  (e.g. PRJEB12345 for BioProject, ERS / SAMEA for BioSample,
--                  SRP / ERP for Study, SRX / ERX for Experiment,
--                  SRR / ERR for Run). Primary key on entity tables; FK on
--                  child tables to identify which entity a row belongs to.
--   alias          Submitter-chosen local name. Stable within a submitter but
--                  not globally unique; INSDC accession is the real key.
--   center_name    Sequencing or submission center that produced the data
--                  (e.g. "BROAD", "WTSI", "BGI").
--   broker_name    Broker submitting on behalf of the center (e.g. "ENA",
--                  "DDBJ"). NULL when the center submits directly.
--   title          Short human-readable label for search and display.
--   description    Longer free-form text.
--   ordinal        0-based position within the parent's collection. Preserves
--                  original XML ordering so the round-trip
--                  XML → SQLite → XML produces equivalent documents.
--   kind           Discriminator for kind-tagged tables: see each column's
--                  CHECK constraint for the enumeration.
--
-- The pragma below must be set on every connection (SQLite ignores it inside a
-- transaction). The F# Internal/Sql.fs helper issues it after opening.
PRAGMA foreign_keys = ON;

-- ============================================================================
-- BioProject
--   A unit of submitted research effort (an organization's sequencing project
--   for a particular goal). Top of the INSDC hierarchy; groups Studies.
-- ============================================================================

CREATE TABLE bioproject (
    accession    TEXT PRIMARY KEY NOT NULL, -- INSDC accession (e.g. PRJNA12345, PRJEB67890)
    alias        TEXT,                      -- submitter's local name
    center_name  TEXT,                      -- producing center
    broker_name  TEXT,                      -- submitting broker, if any
    name         TEXT,                      -- short project name (different from `title`: machine-friendly)
    title        TEXT,                      -- human-readable title (20-250 chars per the XSD)
    description  TEXT,                      -- long-form scope/goals (20-4000 chars per the XSD)
    first_public TEXT                       -- ISO-8601 date the project became publicly visible; NULL = unset
);

CREATE TABLE bioproject_identifiers (
    -- One row per identifier (BioProject.Identifiers.{PrimaryId | SecondaryId[] | ExternalId[] | SubmitterId | Uuid[]}).
    -- A single BioProject has at most one PRIMARY/SUBMITTER but may have many of the others.
    bioproject_accession TEXT NOT NULL REFERENCES bioproject (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    kind                 TEXT NOT NULL CHECK (kind IN ('PRIMARY','SECONDARY','EXTERNAL','SUBMITTER','UUID')),
                            -- which slot in the F# Identifier record this row populates
    ordinal              INTEGER NOT NULL DEFAULT 0, -- 0 for PRIMARY/SUBMITTER singletons; 0..N for the collection kinds
    value                TEXT NOT NULL,             -- the identifier text itself
    label                TEXT,                      -- optional human label (Name.Label / QualifiedName.Label)
    namespace            TEXT,                      -- only meaningful for EXTERNAL/SUBMITTER (QualifiedName.Namespace)
    PRIMARY KEY (bioproject_accession, kind, ordinal)
);

CREATE TABLE bioproject_attributes (
    -- Free-form tag/value/units triples; ontology-extensible metadata.
    bioproject_accession TEXT NOT NULL REFERENCES bioproject (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    ordinal              INTEGER NOT NULL DEFAULT 0, -- preserves declaration order within ProjectAttributes
    tag                  TEXT NOT NULL,             -- attribute name (e.g. "ENA-FIRST-PUBLIC", or an ontology term)
    value                TEXT,                      -- attribute value
    units                TEXT,                      -- optional scientific units string (e.g. "ng/uL")
    PRIMARY KEY (bioproject_accession, ordinal)
);

CREATE TABLE bioproject_links (
    -- Outbound references to other resources (publications, datasets, databases).
    -- One row per Link; the F# Link DU case is captured by `link_kind`, and only the
    -- subset of columns relevant to that case is populated.
    bioproject_accession TEXT NOT NULL REFERENCES bioproject (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    ordinal              INTEGER NOT NULL DEFAULT 0, -- preserves declaration order within ProjectLinks
    link_kind            TEXT NOT NULL CHECK (link_kind IN ('URL','XREF','ENTREZ')),
                            -- URL    = plain http(s) link → populates `url` (+ optional `label`)
                            -- XREF   = cross-reference to an external DB → populates `db`, `id`, `label`
                            -- ENTREZ = NCBI Entrez link → populates `db`, `id`, `query`, `label`
    label                TEXT,                      -- display label, all link kinds
    url                  TEXT,                      -- URL link target (URL only)
    db                   TEXT,                      -- external DB name (XREF / ENTREZ)
    id                   TEXT,                      -- record ID within `db` (XREF / ENTREZ)
    query                TEXT,                      -- Entrez query string (ENTREZ only)
    PRIMARY KEY (bioproject_accession, ordinal)
);

CREATE TABLE bioproject_collaborators (
    -- BioProject.Collaborators is a Collection<string> of plain names; one row per name.
    bioproject_accession TEXT NOT NULL REFERENCES bioproject (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    ordinal              INTEGER NOT NULL DEFAULT 0, -- preserves declaration order
    name                 TEXT NOT NULL,             -- collaborator's free-text name
    PRIMARY KEY (bioproject_accession, ordinal)
);

CREATE TABLE bioproject_related_projects (
    -- BioProject.RelatedProjects: pointers to other INSDC projects with a
    -- hierarchical or peer relationship to this one. Stored as soft TEXT
    -- references — the related project may not be present in our local DB.
    bioproject_accession TEXT NOT NULL REFERENCES bioproject (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    ordinal              INTEGER NOT NULL DEFAULT 0,
    kind                 TEXT NOT NULL CHECK (kind IN ('PARENT','CHILD','PEER')),
                            -- relationship of the related project to this one
    related_accession    TEXT NOT NULL,             -- INSDC accession of the related project (not an FK)
    PRIMARY KEY (bioproject_accession, ordinal)
);

-- TODO (deferred, plan section D2): BioProject.SubmissionProject and
-- BioProject.UmbrellaProject have no representation in this schema yet.
-- They require their own composite + collection tables; out of scope for the
-- initial crawler store.

-- ============================================================================
-- Study
--   The investigation context for sequencing experiments (its goal, abstract,
--   classification). One BioProject can hold many Studies; a Study can also
--   exist without a registered BioProject (bioproject_accession is NULLable).
-- ============================================================================

CREATE TABLE study (
    accession            TEXT PRIMARY KEY NOT NULL, -- INSDC accession (SRP / ERP / DRP)
    alias                TEXT,                      -- submitter's local name
    center_name          TEXT,                      -- producing center
    broker_name          TEXT,                      -- submitting broker
    bioproject_accession TEXT REFERENCES bioproject (accession) ON DELETE SET NULL ON UPDATE CASCADE
                         -- parent BioProject; NULLable because a Study can be ingested before
                         -- its BioProject is known (or may not have one at all)
);

CREATE TABLE study_descriptor (
    -- Study.Descriptor — rich metadata that lives in its own table because most
    -- of these fields are large free-form text or sparsely populated.
    study_accession        TEXT PRIMARY KEY REFERENCES study (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    study_title            TEXT, -- title as it would appear in a publication
    existing_study_type    TEXT, -- controlled-vocabulary type from the SRA "existing" enum
                                  --   (e.g. "Whole Genome Sequencing", "Transcriptome Analysis")
    new_study_type         TEXT, -- free-form type used when none of the controlled values fit
    study_abstract         TEXT, -- summary of the study's goals and scope
    center_name            TEXT, -- (deprecated in XSD but preserved) sequencing center name
    descriptor_center_name TEXT, -- alternative center-name field carried in the descriptor
    center_project_name    TEXT, -- the submitter's internal LIMS project name
    project_id             TEXT, -- (deprecated) NCBI Genome Project accession; superseded by BioProject
    study_description      TEXT  -- extended free-form description, longer than the abstract
);

CREATE TABLE study_identifiers (
    -- See bioproject_identifiers for the column meanings; same shape per owner.
    study_accession TEXT NOT NULL REFERENCES study (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    kind            TEXT NOT NULL CHECK (kind IN ('PRIMARY','SECONDARY','EXTERNAL','SUBMITTER','UUID')),
    ordinal         INTEGER NOT NULL DEFAULT 0,
    value           TEXT NOT NULL,
    label           TEXT,
    namespace       TEXT,
    PRIMARY KEY (study_accession, kind, ordinal)
);

CREATE TABLE study_attributes (
    -- See bioproject_attributes; tag/value/units triples for free-form metadata.
    study_accession TEXT NOT NULL REFERENCES study (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    ordinal         INTEGER NOT NULL DEFAULT 0,
    tag             TEXT NOT NULL,
    value           TEXT,
    units           TEXT,
    PRIMARY KEY (study_accession, ordinal)
);

CREATE TABLE study_links (
    -- See bioproject_links for column meanings and the link_kind enum.
    study_accession TEXT NOT NULL REFERENCES study (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    ordinal         INTEGER NOT NULL DEFAULT 0,
    link_kind       TEXT NOT NULL CHECK (link_kind IN ('URL','XREF','ENTREZ')),
    label           TEXT,
    url             TEXT,
    db              TEXT,
    id              TEXT,
    query           TEXT,
    PRIMARY KEY (study_accession, ordinal)
);

CREATE TABLE study_related_studies (
    -- StudyDescriptor.RelatedStudies: pointers to related studies via a
    -- (db, id, label) triple rather than a hard accession.
    study_accession    TEXT NOT NULL REFERENCES study (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    ordinal            INTEGER NOT NULL DEFAULT 0,
    related_link_db    TEXT,    -- external DB hosting the related study
    related_link_id    TEXT,    -- record id within that DB
    related_link_label TEXT,    -- display label for the relationship
    is_primary         INTEGER NOT NULL DEFAULT 0 CHECK (is_primary IN (0, 1)),
                                -- 1 if this is the canonical related study; 0 otherwise
    PRIMARY KEY (study_accession, ordinal)
);

-- ============================================================================
-- BioSample
--   A biological sample from which sequencing libraries were prepared. Not
--   owned by Study in INSDC — one sample can feed many experiments across
--   studies and projects, so there is no parent FK.
-- ============================================================================

CREATE TABLE biosample (
    accession   TEXT PRIMARY KEY NOT NULL, -- INSDC accession (SAMN / SAMEA / SAMD)
    alias       TEXT,                      -- submitter's local sample name
    center_name TEXT,
    broker_name TEXT,
    title       TEXT,                      -- short label
    description TEXT                       -- free-form description of origin/isolation/treatment
);

CREATE TABLE biosample_name (
    -- BioSample.SampleName: the taxonomic identity of the sample. Required by XSD.
    biosample_accession TEXT PRIMARY KEY REFERENCES biosample (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    taxon_id            INTEGER NOT NULL, -- NCBI Taxonomy ID (the canonical key for the organism)
    scientific_name     TEXT,             -- e.g. "Homo sapiens"
    common_name         TEXT,             -- e.g. "human" — GenBank-style common name
    display_name        TEXT              -- override label for UIs (defaults to scientific name)
);

CREATE TABLE biosample_identifiers (
    -- See bioproject_identifiers for column meanings; same shape per owner.
    biosample_accession TEXT NOT NULL REFERENCES biosample (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    kind                TEXT NOT NULL CHECK (kind IN ('PRIMARY','SECONDARY','EXTERNAL','SUBMITTER','UUID')),
    ordinal             INTEGER NOT NULL DEFAULT 0,
    value               TEXT NOT NULL,
    label               TEXT,
    namespace           TEXT,
    PRIMARY KEY (biosample_accession, kind, ordinal)
);

CREATE TABLE biosample_attributes (
    -- See bioproject_attributes. For BioSamples these are usually MIxS / NCBI
    -- BioSample attribute checklist terms (e.g. "geographic location", "host").
    biosample_accession TEXT NOT NULL REFERENCES biosample (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    ordinal             INTEGER NOT NULL DEFAULT 0,
    tag                 TEXT NOT NULL,
    value               TEXT,
    units               TEXT,
    PRIMARY KEY (biosample_accession, ordinal)
);

CREATE TABLE biosample_links (
    -- See bioproject_links for column meanings and the link_kind enum.
    biosample_accession TEXT NOT NULL REFERENCES biosample (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    ordinal             INTEGER NOT NULL DEFAULT 0,
    link_kind           TEXT NOT NULL CHECK (link_kind IN ('URL','XREF','ENTREZ')),
    label               TEXT,
    url                 TEXT,
    db                  TEXT,
    id                  TEXT,
    query               TEXT,
    PRIMARY KEY (biosample_accession, ordinal)
);

-- ============================================================================
-- Experiment
--   The sequencing assay: a library prepared from a BioSample and run on a
--   specific platform under a parent Study. The connector between samples and
--   raw sequencing data (Runs).
-- ============================================================================

CREATE TABLE experiment (
    accession              TEXT PRIMARY KEY NOT NULL, -- INSDC accession (SRX / ERX / DRX)
    alias                  TEXT,                      -- submitter's local experiment name
    center_name            TEXT,
    broker_name            TEXT,
    title                  TEXT,
    study_accession        TEXT NOT NULL REFERENCES study (accession) ON DELETE CASCADE ON UPDATE CASCADE,
                            -- parent Study; required (every experiment must belong to a study)
    sample_demux_directive TEXT
                            -- Optional sample-level demultiplexing directive carried over
                            -- from the old experiment_processingDirectives table. Free-form
                            -- text; e.g. "leave_as_pool" or "split". NULL when not specified.
);

CREATE TABLE experiment_design (
    -- Experiment.Design — the LibraryDescriptor + DesignDescription bundle.
    experiment_accession          TEXT PRIMARY KEY REFERENCES experiment (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    design_description            TEXT, -- prose explaining goal and library construction
    library_name                  TEXT, -- submitter's name for the library
    library_construction_protocol TEXT, -- prose protocol (or DOI to one)
    library_strategy              TEXT, -- SRA controlled vocab: e.g. WGS, WXS, RNA-Seq, ChIP-Seq, AMPLICON
    library_source                TEXT, -- SRA controlled vocab: GENOMIC, TRANSCRIPTOMIC, METAGENOMIC, ...
    library_selection             TEXT, -- SRA controlled vocab: PCR, RANDOM, cDNA, HMPR, ...
    library_layout_kind           TEXT CHECK (library_layout_kind IN ('SINGLE','PAIRED')),
                                       -- single-end vs paired-end sequencing layout
    library_layout_nominal_length INTEGER, -- paired-end only: expected insert size in bp
    library_layout_nominal_sdev   REAL,    -- paired-end only: expected stddev of insert size
    pooling_strategy              TEXT     -- description of any pooling done during library prep
);

CREATE TABLE experiment_spot_descriptor (
    -- Experiment.Design.SpotDescriptor: how to slice a raw spot (concatenated
    -- reads) into individual logical reads. One row per read in the spot.
    experiment_accession TEXT NOT NULL REFERENCES experiment (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    read_index           INTEGER NOT NULL, -- 0-based position of this read within the spot
    spot_length          INTEGER,          -- total length of the spot in bases (same value on every row)
    item                 TEXT,             -- the XML element name used in the SRA spot decode spec
    read_class           TEXT,             -- "Application Read" | "Technical Read" | "Forward" | "Reverse" | ...
    read_label           TEXT,             -- human label for this read (e.g. "Index", "Linker")
    read_type            TEXT,             -- "Forward" | "Reverse" | "Adapter" | "Linker" | "Primer" | ...
    PRIMARY KEY (experiment_accession, read_index)
);

CREATE TABLE experiment_targeted_loci (
    -- For targeted-sequencing experiments (e.g. 16S rRNA, exome panels): one
    -- row per locus the library targets.
    experiment_accession TEXT NOT NULL REFERENCES experiment (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    locus_name           TEXT NOT NULL, -- e.g. "16S rRNA", or a gene symbol
    probe_set_db         TEXT,          -- DB hosting the probe set definition
    probe_set_id         TEXT,          -- probe-set ID within `probe_set_db`
    probe_set_label      TEXT,          -- display label
    description          TEXT,          -- free-form prose
    PRIMARY KEY (experiment_accession, locus_name)
);

CREATE TABLE experiment_pipeline (
    -- Per-experiment processing pipeline: an ordered sequence of steps run
    -- against the raw output (basecalling, trimming, mapping, ...).
    experiment_accession   TEXT NOT NULL REFERENCES experiment (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    step_ordinal           INTEGER NOT NULL, -- 0-based step position within the pipeline
    pipeline_section_name  TEXT,    -- which section of the pipeline this step belongs to
    pipeline_step_ind      TEXT,    -- submitter's identifier for this step (free-form, not a sequence number)
    pipeline_prev_step_ind TEXT,    -- pipeline_step_ind of the step this one consumes input from
    pipeline_program       TEXT,    -- program name (e.g. "BWA-MEM")
    pipeline_version       TEXT,    -- program version (e.g. "0.7.17")
    pipeline_notes         TEXT,    -- free-form notes about this step
    PRIMARY KEY (experiment_accession, step_ordinal)
);

CREATE TABLE experiment_platform (
    -- Experiment.Platform — F# Platform is a DU over 18 sequencing technologies.
    -- `kind` records which case it is; per-platform parameters live in the
    -- sibling experiment_platform_params table (key/value bag).
    experiment_accession TEXT PRIMARY KEY REFERENCES experiment (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    kind                 TEXT NOT NULL CHECK (kind IN (
                             'LS454','ILLUMINA','HELICOS','ABI_SOLID','COMPLETE_GENOMICS',
                             'BGISEQ','OXFORD_NANOPORE','PACBIO_SMRT','ION_TORRENT','CAPILLARY',
                             'DNBSEQ','ELEMENT','AVITI','ULTIMA','VELA_DIAGNOSTICS',
                             'GENAPSYS','GENEMIND','TAPESTRI')),
                            -- sequencing technology family; values match the XSD's PLATFORM
                            -- choice element names (e.g. ILLUMINA, PACBIO_SMRT)
    instrument_model     TEXT
                            -- specific instrument model, e.g. "Illumina NovaSeq 6000",
                            -- "PacBio Sequel II". Values come from the per-platform XSD enums.
);

CREATE TABLE experiment_platform_params (
    -- Per-platform configuration not common to every platform DU case
    -- (e.g. PacBio "key_sequence" or Oxford Nanopore "flowcell_type").
    experiment_accession TEXT NOT NULL REFERENCES experiment_platform (experiment_accession) ON DELETE CASCADE ON UPDATE CASCADE,
    key                  TEXT NOT NULL, -- parameter name as it appears in the platform XSD
    value                TEXT,          -- serialized parameter value
    PRIMARY KEY (experiment_accession, key)
);

CREATE TABLE experiment_identifiers (
    -- See bioproject_identifiers for column meanings; same shape per owner.
    experiment_accession TEXT NOT NULL REFERENCES experiment (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    kind                 TEXT NOT NULL CHECK (kind IN ('PRIMARY','SECONDARY','EXTERNAL','SUBMITTER','UUID')),
    ordinal              INTEGER NOT NULL DEFAULT 0,
    value                TEXT NOT NULL,
    label                TEXT,
    namespace            TEXT,
    PRIMARY KEY (experiment_accession, kind, ordinal)
);

CREATE TABLE experiment_attributes (
    -- See bioproject_attributes; tag/value/units triples.
    experiment_accession TEXT NOT NULL REFERENCES experiment (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    ordinal              INTEGER NOT NULL DEFAULT 0,
    tag                  TEXT NOT NULL,
    value                TEXT,
    units                TEXT,
    PRIMARY KEY (experiment_accession, ordinal)
);

CREATE TABLE experiment_links (
    -- See bioproject_links for column meanings and the link_kind enum.
    experiment_accession TEXT NOT NULL REFERENCES experiment (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    ordinal              INTEGER NOT NULL DEFAULT 0,
    link_kind            TEXT NOT NULL CHECK (link_kind IN ('URL','XREF','ENTREZ')),
    label                TEXT,
    url                  TEXT,
    db                   TEXT,
    id                   TEXT,
    query                TEXT,
    PRIMARY KEY (experiment_accession, ordinal)
);

-- ---- Experiment.StudyRef ----------------------------------------------------
-- Reference to the parent Study expressed as an INSDC RefObject — the F# type
-- supports three resolution strategies (accession, refname+refcenter, IDENTIFIERS).
-- Stored separately from `experiment.study_accession` so the original reference
-- payload survives even when the accession alone is enough to navigate the
-- hierarchy. `accession` here is a SOFT FK (ON DELETE SET NULL) because INSDC
-- documents may omit it when only refname/refcenter is supplied.

CREATE TABLE experiment_study_ref (
    experiment_accession TEXT PRIMARY KEY REFERENCES experiment (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    accession            TEXT REFERENCES study (accession) ON DELETE SET NULL ON UPDATE CASCADE,
                            -- soft FK to study; NULL allowed when reference uses refname instead
    refname              TEXT, -- name of the study within a namespace (used when accession is unknown)
    refcenter            TEXT  -- namespace identifier; pairs with refname (e.g. center that issued the name)
);

CREATE TABLE experiment_study_ref_identifiers (
    -- Identifier collection nested INSIDE the StudyRef (RefObject.Identifiers).
    -- Distinct from experiment_identifiers, which describes the experiment itself.
    experiment_accession TEXT NOT NULL REFERENCES experiment_study_ref (experiment_accession) ON DELETE CASCADE ON UPDATE CASCADE,
    kind                 TEXT NOT NULL CHECK (kind IN ('PRIMARY','SECONDARY','EXTERNAL','SUBMITTER','UUID')),
    ordinal              INTEGER NOT NULL DEFAULT 0,
    value                TEXT NOT NULL,
    label                TEXT,
    namespace            TEXT,
    PRIMARY KEY (experiment_accession, kind, ordinal)
);

-- ---- Experiment.Design.SampleDescriptor ------------------------------------
-- Reference to the BioSample the experiment's library was prepared from.
-- Same RefObject shape as experiment_study_ref (soft FK + refname/refcenter +
-- nested identifiers table).

CREATE TABLE experiment_sample_descriptor (
    experiment_accession TEXT PRIMARY KEY REFERENCES experiment (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    accession            TEXT REFERENCES biosample (accession) ON DELETE SET NULL ON UPDATE CASCADE,
                            -- soft FK to biosample; NULL allowed when reference uses refname instead
    refname              TEXT,
    refcenter            TEXT
);

CREATE TABLE experiment_sample_descriptor_identifiers (
    -- Identifier collection nested INSIDE the SampleDescriptor.
    experiment_accession TEXT NOT NULL REFERENCES experiment_sample_descriptor (experiment_accession) ON DELETE CASCADE ON UPDATE CASCADE,
    kind                 TEXT NOT NULL CHECK (kind IN ('PRIMARY','SECONDARY','EXTERNAL','SUBMITTER','UUID')),
    ordinal              INTEGER NOT NULL DEFAULT 0,
    value                TEXT NOT NULL,
    label                TEXT,
    namespace            TEXT,
    PRIMARY KEY (experiment_accession, kind, ordinal)
);

-- ============================================================================
-- Run
--   The actual sequencing-machine execution that produced raw data for an
--   Experiment. Carries the data files and run-time metadata.
-- ============================================================================

CREATE TABLE run (
    accession              TEXT PRIMARY KEY NOT NULL, -- INSDC accession (SRR / ERR / DRR)
    alias                  TEXT,                      -- submitter's local run name
    center_name            TEXT,
    broker_name            TEXT,
    title                  TEXT,
    experiment_accession   TEXT NOT NULL REFERENCES experiment (accession) ON DELETE CASCADE ON UPDATE CASCADE,
                            -- parent Experiment; required
    run_center             TEXT,                      -- contract sequencing center that physically did the run
    run_date               TEXT,                      -- ISO-8601 datetime when the sequencing run started; NULL = unset
    sample_demux_directive TEXT
                            -- Optional sample-level demultiplexing directive carried over from
                            -- the old run_processingDirectives table. Mirrors the field on
                            -- `experiment` for runs whose demux differs from the experiment default.
);

CREATE TABLE run_data_block (
    -- Run.DataBlock — describes the file payload (and member name for pooled
    -- runs). Most runs have at most one logical data block, so this is a
    -- 1:1 child rather than a collection.
    run_accession TEXT PRIMARY KEY REFERENCES run (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    member_name   TEXT, -- when the run is one member of a pooled sequencing event,
                        -- the identifier of this member within the pool
    files         TEXT  -- serialized representation of the contained FILES collection;
                        -- a fuller normalized child table is out of scope for this phase
);

CREATE TABLE run_spot_descriptor (
    -- Same shape and semantics as experiment_spot_descriptor — Run may carry
    -- its own spot decoding when it differs from the parent Experiment.
    run_accession TEXT NOT NULL REFERENCES run (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    read_index    INTEGER NOT NULL,
    spot_length   INTEGER,
    item          TEXT,
    read_class    TEXT,
    read_label    TEXT,
    read_type     TEXT,
    PRIMARY KEY (run_accession, read_index)
);

CREATE TABLE run_processing_pipeline (
    -- Same shape and semantics as experiment_pipeline; describes the
    -- post-run processing chain specific to this run.
    run_accession          TEXT NOT NULL REFERENCES run (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    step_ordinal           INTEGER NOT NULL,
    pipeline_section_name  TEXT,
    pipeline_step_ind      TEXT,
    pipeline_prev_step_ind TEXT,
    pipeline_program       TEXT,
    pipeline_version       TEXT,
    pipeline_notes         TEXT,
    PRIMARY KEY (run_accession, step_ordinal)
);

CREATE TABLE run_platform (
    -- Run.Platform — optional; present when the run-time platform differs from
    -- the parent Experiment's. Same shape as experiment_platform.
    run_accession    TEXT PRIMARY KEY REFERENCES run (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    kind             TEXT NOT NULL CHECK (kind IN (
                         'LS454','ILLUMINA','HELICOS','ABI_SOLID','COMPLETE_GENOMICS',
                         'BGISEQ','OXFORD_NANOPORE','PACBIO_SMRT','ION_TORRENT','CAPILLARY',
                         'DNBSEQ','ELEMENT','AVITI','ULTIMA','VELA_DIAGNOSTICS',
                         'GENAPSYS','GENEMIND','TAPESTRI')),
    instrument_model TEXT
);

CREATE TABLE run_platform_params (
    -- Per-platform parameter bag for the run-level platform; same shape as
    -- experiment_platform_params.
    run_accession TEXT NOT NULL REFERENCES run_platform (run_accession) ON DELETE CASCADE ON UPDATE CASCADE,
    key           TEXT NOT NULL,
    value         TEXT,
    PRIMARY KEY (run_accession, key)
);

CREATE TABLE run_identifiers (
    -- See bioproject_identifiers for column meanings; same shape per owner.
    run_accession TEXT NOT NULL REFERENCES run (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    kind          TEXT NOT NULL CHECK (kind IN ('PRIMARY','SECONDARY','EXTERNAL','SUBMITTER','UUID')),
    ordinal       INTEGER NOT NULL DEFAULT 0,
    value         TEXT NOT NULL,
    label         TEXT,
    namespace     TEXT,
    PRIMARY KEY (run_accession, kind, ordinal)
);

CREATE TABLE run_attributes (
    -- See bioproject_attributes; tag/value/units triples.
    run_accession TEXT NOT NULL REFERENCES run (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    ordinal       INTEGER NOT NULL DEFAULT 0,
    tag           TEXT NOT NULL,
    value         TEXT,
    units         TEXT,
    PRIMARY KEY (run_accession, ordinal)
);

CREATE TABLE run_links (
    -- See bioproject_links for column meanings and the link_kind enum.
    run_accession TEXT NOT NULL REFERENCES run (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    ordinal       INTEGER NOT NULL DEFAULT 0,
    link_kind     TEXT NOT NULL CHECK (link_kind IN ('URL','XREF','ENTREZ')),
    label         TEXT,
    url           TEXT,
    db            TEXT,
    id            TEXT,
    query         TEXT,
    PRIMARY KEY (run_accession, ordinal)
);

-- ---- Run.ExperimentRef -----------------------------------------------------
-- Reference to the parent Experiment. Same RefObject shape as the experiment-
-- side reference tables (soft FK + refname/refcenter + nested identifiers).

CREATE TABLE run_experiment_ref (
    run_accession TEXT PRIMARY KEY REFERENCES run (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    accession     TEXT REFERENCES experiment (accession) ON DELETE SET NULL ON UPDATE CASCADE,
                  -- soft FK to experiment; NULL allowed when reference uses refname instead
    refname       TEXT,
    refcenter     TEXT
);

CREATE TABLE run_experiment_ref_identifiers (
    -- Identifier collection nested INSIDE the ExperimentRef.
    run_accession TEXT NOT NULL REFERENCES run_experiment_ref (run_accession) ON DELETE CASCADE ON UPDATE CASCADE,
    kind          TEXT NOT NULL CHECK (kind IN ('PRIMARY','SECONDARY','EXTERNAL','SUBMITTER','UUID')),
    ordinal       INTEGER NOT NULL DEFAULT 0,
    value         TEXT NOT NULL,
    label         TEXT,
    namespace     TEXT,
    PRIMARY KEY (run_accession, kind, ordinal)
);

-- TODO (deferred, plan section D4): Run.RunProperty (RunRunType) and the full
-- Run.Processing tree have only partial coverage here (the pipeline + demux
-- directive). Full mapping is out of scope for this phase.

-- ============================================================================
-- Indexes on every foreign-key column.
--   Primary-key columns are already indexed by SQLite. These indexes cover the
--   non-PK FK columns that joins traverse, so parent → children navigation
--   stays cheap.
-- ============================================================================

-- Hierarchy parent FKs:
CREATE INDEX ix_study_bioproject              ON study      (bioproject_accession);
CREATE INDEX ix_experiment_study              ON experiment (study_accession);
CREATE INDEX ix_run_experiment                ON run        (experiment_accession);

-- Identifier table FKs already covered by the (owner, kind, ordinal) PK;
-- the secondary indexes below let queries like "find all PRIMARY identifiers
-- for owner X" hit a covering index directly:
CREATE INDEX ix_bioproject_identifiers_kind   ON bioproject_identifiers (bioproject_accession, kind);
CREATE INDEX ix_study_identifiers_kind        ON study_identifiers      (study_accession,      kind);
CREATE INDEX ix_biosample_identifiers_kind    ON biosample_identifiers  (biosample_accession,  kind);
CREATE INDEX ix_experiment_identifiers_kind   ON experiment_identifiers (experiment_accession, kind);
CREATE INDEX ix_run_identifiers_kind          ON run_identifiers        (run_accession,        kind);

-- Soft-FK targets on reference tables (lets "which experiments reference
-- study S?" queries hit an index instead of scanning):
CREATE INDEX ix_experiment_study_ref_target            ON experiment_study_ref            (accession);
CREATE INDEX ix_experiment_sample_descriptor_target    ON experiment_sample_descriptor    (accession);
CREATE INDEX ix_run_experiment_ref_target              ON run_experiment_ref              (accession);
