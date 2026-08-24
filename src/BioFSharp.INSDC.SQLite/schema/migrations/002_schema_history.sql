-- Schema version 2: make the ordered migration lineage queryable without
-- relying solely on PRAGMA user_version. Dates are deliberately omitted so
-- applying the same migration produces deterministic schema content.
CREATE TABLE insdc_schema_history (
    version      INTEGER PRIMARY KEY NOT NULL,
    description TEXT NOT NULL
);

-- The original spot-descriptor tables omitted the common BASE_COORD choice,
-- so real Experiment/Run fixtures could not round-trip their read starts.
ALTER TABLE experiment_spot_descriptor ADD COLUMN base_coord INTEGER;
ALTER TABLE run_spot_descriptor ADD COLUMN base_coord INTEGER;

-- Preserve BioProject's two nested project-composition cases instead of
-- dropping them during deconstruction.
CREATE TABLE bioproject_submission_project (
    bioproject_accession TEXT PRIMARY KEY NOT NULL REFERENCES bioproject (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    has_organism        INTEGER NOT NULL CHECK (has_organism IN (0, 1)),
    taxon_id            INTEGER,
    scientific_name     TEXT,
    common_name         TEXT,
    strain              TEXT,
    breed               TEXT,
    cultivar            TEXT,
    isolate             TEXT
);

CREATE TABLE bioproject_submission_locus_tags (
    bioproject_accession TEXT NOT NULL REFERENCES bioproject_submission_project (bioproject_accession) ON DELETE CASCADE ON UPDATE CASCADE,
    ordinal              INTEGER NOT NULL,
    locus_tag_prefix     TEXT,
    PRIMARY KEY (bioproject_accession, ordinal)
);

CREATE TABLE bioproject_umbrella_project (
    bioproject_accession TEXT PRIMARY KEY NOT NULL REFERENCES bioproject (accession) ON DELETE CASCADE ON UPDATE CASCADE,
    has_organism        INTEGER NOT NULL CHECK (has_organism IN (0, 1)),
    taxon_id            INTEGER,
    scientific_name     TEXT,
    common_name         TEXT,
    strain              TEXT,
    breed               TEXT,
    cultivar            TEXT,
    isolate             TEXT
);

INSERT INTO insdc_schema_history (version, description) VALUES
    (1, 'initial normalized INSDC schema'),
    (2, 'complete spot descriptors and BioProject composition');
