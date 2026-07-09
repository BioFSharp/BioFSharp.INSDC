namespace BioFSharp.INSDC.Crawler

open System
open BioFSharp.FileFormats.INSDC

/// The connected INSDC records discovered for a root accession. `BioProjects`
/// may be empty (a study with no umbrella project); `Studies` is surfaced
/// independently for exactly that reason.
type CrawlResult =
    {
        /// BioProject records (PRJ...). May be empty.
        BioProjects: BioProject[]
        /// Study records (SRP/ERP/DRP).
        Studies: Study[]
        /// BioSample records.
        BioSamples: BioSample[]
        /// Experiment records.
        Experiments: Experiment[]
        /// Run records.
        Runs: Run[]
    }

/// Helpers over `CrawlResult`.
[<RequireQualifiedAccess>]
module CrawlResult =

    /// The records as the plain tuple
    /// `BioProject[] * Study[] * BioSample[] * Experiment[] * Run[]`.
    let toTuple (result: CrawlResult) =
        result.BioProjects, result.Studies, result.BioSamples, result.Experiments, result.Runs

/// Batched fetch + parse of the discovered accessions into typed records.
/// Scopes `open BioFSharp.IO.INSDC` so the IO reader modules (e.g. `BioProject`)
/// do not collide with the identically named SQLite modules used by `Persist`.
module private Fetch =

    open BioFSharp.IO.INSDC

    /// `Async.Parallel` with bounded concurrency and a per-item throttle.
    let private mapBounded (options: CrawlOptions) (f: 'a -> Async<'b>) (items: 'a list) : Async<'b[]> =
        let throttled item =
            async {
                if options.ThrottleMs > 0 then
                    do! Async.Sleep options.ThrottleMs

                return! f item
            }

        Async.Parallel(items |> List.map throttled, max 1 options.MaxConcurrency)

    /// Fetches every accession for one entity `kind` in chunked Browser API
    /// batches (each returns a `*_SET`) and parses them with `readString`. A
    /// failed batch is logged and yields no records rather than aborting.
    let entity
        (options: CrawlOptions)
        (kind: string)
        (readString: string -> seq<'T>)
        (accessions: string list)
        : Async<'T[]> =
        async {
            if List.isEmpty accessions then
                return [||]
            else
                options.Log(Fetching(kind, List.length accessions))
                let fetch = Internal.Http.withRetry options.Retries options.Log options.Fetch

                let! batches =
                    accessions
                    |> List.chunkBySize (max 1 options.ChunkSize)
                    |> mapBounded options (fun batch ->
                        async {
                            let url = Endpoints.browserXml options.BrowserBaseUrl batch

                            try
                                let! xml = fetch url
                                return readString xml |> Seq.toArray
                            with ex ->
                                options.Log(Failed(sprintf "fetch %s [%s]" kind (String.concat "," batch), ex.Message))
                                return [||]
                        })

                let records = batches |> Array.collect id
                options.Log(Parsed(kind, records.Length))
                return records
        }

    /// Discovers the connected set for `rootAccession`, then fetches + parses
    /// every entity kind. Returns the records and the discovery set (the latter
    /// carries the relationships persistence needs to thread foreign keys).
    let crawlCore (options: CrawlOptions) (rootAccession: string) : Async<CrawlResult * DiscoveredSet> =
        async {
            options.Log(Started rootAccession)
            let! discovered = Discovery.discoverAsync options rootAccession

            options.Log(
                Discovered(
                    Map.ofList
                        [ "BioProject", List.length discovered.BioProjects
                          "Study", List.length discovered.Studies
                          "BioSample", List.length discovered.BioSamples
                          "Experiment", List.length discovered.Experiments
                          "Run", List.length discovered.Runs ]
                )
            )

            let! bioProjects = entity options "BioProject" BioProject.readString discovered.BioProjects
            let! studies = entity options "Study" Study.readString discovered.Studies
            let! bioSamples = entity options "BioSample" BioSample.readString discovered.BioSamples
            let! experiments = entity options "Experiment" Experiment.readString discovered.Experiments
            let! runs = entity options "Run" Run.readString discovered.Runs

            let result =
                {
                    BioProjects = bioProjects
                    Studies = studies
                    BioSamples = bioSamples
                    Experiments = experiments
                    Runs = runs
                }

            return result, discovered
        }

/// Persists a crawl into SQLite via the `BioFSharp.INSDC.SQLite` store. Scopes
/// `open BioFSharp.INSDC.SQLite` so its entity modules do not collide with the
/// IO reader modules used by `Fetch`.
module private Persist =

    open Microsoft.Data.Sqlite
    open BioFSharp.INSDC.SQLite

    // Just the transaction helper — a module abbreviation rather than `open
    // ...Internal`, whose `Schema` would shadow the public `Schema.init` used
    // below.
    module Sql = BioFSharp.INSDC.SQLite.Internal.Sql

    /// Opens (creating the file if needed) a SQLite connection for the crawl.
    /// Foreign-key enforcement is turned OFF for the insert pass in `persist`
    /// (the bundled schema's `PRAGMA foreign_keys = ON` would otherwise reject
    /// the store's legitimately dangling soft references — see `persist`).
    let openConnection (sqlitePath: string) : SqliteConnection =
        let connection = new SqliteConnection(sprintf "Data Source=%s" sqlitePath)
        connection.Open()
        connection

    /// Turns foreign-key enforcement off on `connection`. Must run *after*
    /// `Schema.init`, whose SQL includes `PRAGMA foreign_keys = ON`.
    let private disableForeignKeys (connection: SqliteConnection) : unit =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- "PRAGMA foreign_keys = OFF;"
        cmd.ExecuteNonQuery() |> ignore

    /// True when the bundled schema has already been applied (the `bioproject`
    /// table exists) — used to init only a fresh database.
    let private schemaApplied (connection: SqliteConnection) : bool =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'bioproject' LIMIT 1;"
        use reader = cmd.ExecuteReader()
        reader.Read()

    /// Inserts each of `records` whose accession is not already in `existing`,
    /// returning (inserted, skipped) and adding inserted accessions to `existing`.
    let private insertNew
        (existing: System.Collections.Generic.HashSet<string>)
        (accessionOf: 'T -> string)
        (insert: 'T -> unit)
        (records: 'T[])
        : int * int =
        let mutable inserted = 0
        let mutable skipped = 0

        for record in records do
            let accession = accessionOf record

            if existing.Contains accession then
                skipped <- skipped + 1
            else
                insert record
                existing.Add accession |> ignore
                inserted <- inserted + 1

        inserted, skipped

    /// Persists the crawl into `connection`: the connectivity relations first
    /// (no FKs, so they land regardless of record coverage), then the entity
    /// records in FK order (project -> study -> sample -> experiment -> run),
    /// threading parent accessions from `discovered` and skipping accessions
    /// already stored (idempotent resume).
    let persist
        (options: CrawlOptions)
        (connection: SqliteConnection)
        (rootAccession: string)
        (result: CrawlResult)
        (discovered: DiscoveredSet)
        : unit =
        if not (schemaApplied connection) then
            Schema.init connection

        // Run the inserts without FK enforcement: the store's soft references
        // legitimately dangle in a crawl — an experiment's SAMPLE_DESCRIPTOR
        // points at the SRA sample accession (DRS...) while the sample row is
        // keyed by its BioSample accession (SAMD..., what ENA's filereport
        // returns), the same sample under two accessions — and a crawl may hold
        // only part of a project. Hard, NOT NULL FK order (study -> experiment
        // -> run) is guaranteed by the insertion order below instead.
        disableForeignKeys connection

        // One surrounding transaction for the whole insert pass. Each entity
        // `insert` opens its own `Sql.withTransaction`, but that is reentrant
        // and joins this one, so the ~half-million per-record commits collapse
        // into a single commit at the end — the difference between a trickle and
        // a bulk load. `disableForeignKeys` stays *outside* it: `PRAGMA
        // foreign_keys` is a no-op while a transaction is open.
        Sql.withTransaction connection (fun _tx ->

        let hashSet (accessions: string seq) =
            System.Collections.Generic.HashSet<string>(accessions)

        // Connectivity relations — no foreign keys, safe to write even when a
        // referenced record's XML was never fetched.
        let fetchedAt = DateTime.UtcNow.ToString("o")

        for row in discovered.Rows do
            AccessionRelations.insert
                connection
                {
                    RunAccession = row.RunAccession
                    ExperimentAccession = row.ExperimentAccession
                    SampleAccession = row.SampleAccession
                    StudyAccession = row.StudyAccession
                    ProjectAccession = row.ProjectAccession
                    RootAccession = rootAccession
                    FetchedAt = fetchedAt
                }

        options.Log(Persisted("accession_relations", List.length discovered.Rows, 0))

        // BioProject (top of the ownership chain).
        let projects = hashSet (BioProject.listAccessions connection)

        let pIns, pSkip =
            insertNew projects (fun (p: BioProject) -> p.Accession) (BioProject.insert connection) result.BioProjects

        options.Log(Persisted("BioProject", pIns, pSkip))

        // Study (parent project nullable — only linked when its row exists).
        let studies = hashSet (Study.listAccessions connection)
        let mutable sIns, sSkip = 0, 0

        for study in result.Studies do
            if studies.Contains study.Accession then
                sSkip <- sSkip + 1
            else
                let parentProject =
                    match Map.tryFind study.Accession discovered.StudyToProject with
                    | Some project when projects.Contains project -> project
                    | _ -> null

                Study.insert connection parentProject study
                studies.Add study.Accession |> ignore
                sIns <- sIns + 1

        options.Log(Persisted("Study", sIns, sSkip))

        // BioSample (before experiments so the sample-descriptor soft FK resolves).
        let samples = hashSet (BioSample.listAccessions connection)

        let smIns, smSkip =
            insertNew samples (fun (s: BioSample) -> s.Accession) (BioSample.insert connection) result.BioSamples

        options.Log(Persisted("BioSample", smIns, smSkip))

        // Experiment (parent study NOT NULL).
        let experiments = hashSet (Experiment.listAccessions connection)
        let mutable eIns, eSkip = 0, 0

        for experiment in result.Experiments do
            if experiments.Contains experiment.Accession then
                eSkip <- eSkip + 1
            else
                match Map.tryFind experiment.Accession discovered.ExperimentToStudy with
                | Some study when studies.Contains study ->
                    Experiment.insert connection study experiment
                    experiments.Add experiment.Accession |> ignore
                    eIns <- eIns + 1
                | _ -> options.Log(Failed(sprintf "persist Experiment %s" experiment.Accession, "no stored parent study"))

        options.Log(Persisted("Experiment", eIns, eSkip))

        // Run (parent experiment NOT NULL).
        let runs = hashSet (Run.listAccessions connection)
        let mutable rIns, rSkip = 0, 0

        for run in result.Runs do
            if runs.Contains run.Accession then
                rSkip <- rSkip + 1
            else
                match Map.tryFind run.Accession discovered.RunToExperiment with
                | Some experiment when experiments.Contains experiment ->
                    Run.insert connection experiment run
                    runs.Add run.Accession |> ignore
                    rIns <- rIns + 1
                | _ -> options.Log(Failed(sprintf "persist Run %s" run.Accession, "no stored parent experiment"))

        options.Log(Persisted("Run", rIns, rSkip)))

/// The public crawler API. Two surfaces: return the connected records, or
/// persist them (and the connectivity relations) into a SQLite database.
[<RequireQualifiedAccess>]
module Crawler =

    /// Crawls every record connected to `accession` (a project or study
    /// accession) using `options`, returning the typed records.
    let crawlWithAsync (options: CrawlOptions) (accession: string) : Async<CrawlResult> =
        async {
            let! result, _ = Fetch.crawlCore options accession

            let total =
                result.BioProjects.Length
                + result.Studies.Length
                + result.BioSamples.Length
                + result.Experiments.Length
                + result.Runs.Length

            options.Log(Completed(sprintf "%s -> %d record(s)" accession total))
            return result
        }

    /// Crawls every record connected to `accession` with `CrawlOptions.Default`.
    let crawlAsync (accession: string) : Async<CrawlResult> =
        crawlWithAsync CrawlOptions.Default accession

    /// Blocking `crawlAsync` — the `projectAccession -> CrawlResult` surface.
    let crawl (accession: string) : CrawlResult =
        crawlAsync accession |> Async.RunSynchronously

    /// Crawls `accession` with `CrawlOptions.Default` and returns the records
    /// together with the discovery set — the per-run connectivity rows (incl.
    /// the FASTQ files) and parent maps that the records alone do not carry.
    /// Useful for building views such as an ArcIR graph.
    let crawlAndDiscoverAsync (accession: string) : Async<CrawlResult * DiscoveredSet> =
        Fetch.crawlCore CrawlOptions.Default accession

    /// Blocking `crawlAndDiscoverAsync`.
    let crawlAndDiscover (accession: string) : CrawlResult * DiscoveredSet =
        crawlAndDiscoverAsync accession |> Async.RunSynchronously

    /// Crawls `accession` using `options` and persists the records (and the
    /// connectivity relations) into the SQLite database at `sqlitePath`,
    /// creating + initializing it if needed.
    let crawlToSqliteWithAsync (options: CrawlOptions) (accession: string) (sqlitePath: string) : Async<unit> =
        async {
            let! result, discovered = Fetch.crawlCore options accession
            use connection = Persist.openConnection sqlitePath
            Persist.persist options connection accession result discovered

            let summary =
                sprintf
                    "%s -> %s (%d projects, %d studies, %d samples, %d experiments, %d runs)"
                    accession
                    sqlitePath
                    result.BioProjects.Length
                    result.Studies.Length
                    result.BioSamples.Length
                    result.Experiments.Length
                    result.Runs.Length

            options.Log(Completed summary)
        }

    /// Crawls `accession` with `CrawlOptions.Default` and persists to `sqlitePath`.
    let crawlToSqliteAsync (accession: string) (sqlitePath: string) : Async<unit> =
        crawlToSqliteWithAsync CrawlOptions.Default accession sqlitePath

    /// Blocking `crawlToSqliteAsync` — the `projectAccession -> sqlitePath -> ()`
    /// surface.
    let crawlToSqlite (accession: string) (sqlitePath: string) : unit =
        crawlToSqliteAsync accession sqlitePath |> Async.RunSynchronously
