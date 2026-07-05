namespace BioFSharp.INSDC.SQLite

open Microsoft.Data.Sqlite
open BioFSharp.INSDC.SQLite.Internal

/// A single relation between accessions — the flat connectivity tuple that
/// links a run to its experiment, sample, study, and project, as captured from
/// the ENA Portal API `read_run` report. Column naming is clarified from ENA's:
/// `StudyAccession` is ENA's `secondary_study_accession` (the SRP/ERP/DRP Study)
/// and `ProjectAccession` is ENA's `study_accession` (the PRJ... BioProject).
type AccessionRelation =
    {
        /// The run this relation is keyed by (SRR/ERR/DRR).
        RunAccession: string
        /// The experiment the run belongs to (SRX/ERX/DRX).
        ExperimentAccession: string
        /// The sample the run's library was prepared from (SAMN/SAMEA/SAMD).
        SampleAccession: string
        /// The Study (SRP/ERP/DRP) — ENA's `secondary_study_accession`.
        StudyAccession: string
        /// The BioProject (PRJ...) — ENA's `study_accession`.
        ProjectAccession: string
        /// The accession the crawl started from.
        RootAccession: string
        /// ISO-8601 timestamp of when discovery fetched this relation.
        FetchedAt: string
    }

/// SQLite persistence for the `accession_relations` table. Unlike the entity
/// modules this stores a flat connectivity row verbatim: it has no nested owner
/// tables and no foreign keys, so a relation can be recorded even when the
/// referenced records were never downloaded.
module AccessionRelations =

    [<Literal>]
    let private selectColumns =
        "SELECT run_accession, experiment_accession, sample_accession, \
                study_accession, project_accession, root_accession, fetched_at \
         FROM accession_relations"

    /// Persists one accession relation. Re-inserting the same `RunAccession`
    /// replaces the previous row (`INSERT OR REPLACE`), so a re-crawl refreshes
    /// `RootAccession`/`FetchedAt` instead of erroring.
    let insert (connection: SqliteConnection) (relation: AccessionRelation) : unit =
        Sql.execNonQuery
            connection
            "INSERT OR REPLACE INTO accession_relations \
                (run_accession, experiment_accession, sample_accession, \
                 study_accession, project_accession, root_accession, fetched_at) \
             VALUES (@run, @exp, @sample, @study, @project, @root, @fetched);"
            [
                "@run", box relation.RunAccession
                "@exp", box relation.ExperimentAccession
                "@sample", box relation.SampleAccession
                "@study", box relation.StudyAccession
                "@project", box relation.ProjectAccession
                "@root", box relation.RootAccession
                "@fetched", box relation.FetchedAt
            ]
        |> ignore

    let private readRow (reader: SqliteDataReader) : AccessionRelation =
        {
            RunAccession = Sql.readStringOrNull reader 0
            ExperimentAccession = Sql.readStringOrNull reader 1
            SampleAccession = Sql.readStringOrNull reader 2
            StudyAccession = Sql.readStringOrNull reader 3
            ProjectAccession = Sql.readStringOrNull reader 4
            RootAccession = Sql.readStringOrNull reader 5
            FetchedAt = Sql.readStringOrNull reader 6
        }

    /// Reconstructs the relation keyed by `runAccession`, or `None` if absent.
    let tryGet (connection: SqliteConnection) (runAccession: string) : AccessionRelation option =
        Sql.tryQueryOne
            connection
            (selectColumns + " WHERE run_accession = @run;")
            [ "@run", box runAccession ]
            readRow

    /// Every relation whose `project_accession` matches `projectAccession`,
    /// ordered by run — the project's runs/samples/experiments in one query.
    let listByProject (connection: SqliteConnection) (projectAccession: string) : AccessionRelation list =
        Sql.queryAll
            connection
            (selectColumns + " WHERE project_accession = @project ORDER BY run_accession;")
            [ "@project", box projectAccession ]
            readRow

    /// Every relation whose `sample_accession` matches `sampleAccession`,
    /// ordered by run — resolves the sample to its project(s)/experiments/runs
    /// without a four-way join.
    let listBySample (connection: SqliteConnection) (sampleAccession: string) : AccessionRelation list =
        Sql.queryAll
            connection
            (selectColumns + " WHERE sample_accession = @sample ORDER BY run_accession;")
            [ "@sample", box sampleAccession ]
            readRow

    /// Lists every run accession present in `accession_relations`,
    /// lexicographically. Suitable for crawler resume / dedup bookkeeping.
    let listAccessions (connection: SqliteConnection) : string seq =
        Sql.queryAll
            connection
            "SELECT run_accession FROM accession_relations ORDER BY run_accession;"
            []
            (fun reader -> reader.GetString 0)
        :> string seq
