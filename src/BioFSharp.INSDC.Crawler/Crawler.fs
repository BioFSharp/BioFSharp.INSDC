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

/// What an INSDC project accession resolves to for the services downstream of it.
///
/// A paper lookup and a DEE2 lookup both key on things only the INSDC records know
/// — the project's publication cross-references, and its SRA study accession — but
/// neither one ever reads a BioSample, an Experiment or a Run. `InsdcRefs` is that
/// middle ground: the records those lookups actually consume, resolved once and
/// reusable. `Crawler.paperRefs` and `Crawler.dee2Key` project it into the ids
/// EuropePMC and DEE2 are respectively keyed on.
///
/// Produced either by `Crawler.resolve` (the lean fetch — BioProject + Study only)
/// or by `Crawler.refsOf` over a full `crawl`/`crawlAndDiscover`, so the same two
/// projections work whichever way the records were obtained.
type InsdcRefs =
    {
        /// The root accession these refs were resolved from.
        Accession: string
        /// The BioProject records discovered. May be empty (a study with no
        /// umbrella project).
        BioProjects: BioProject[]
        /// The Study records discovered. May be empty.
        Studies: Study[]
        /// The connectivity set discovery returned — the per-run rows and parent
        /// maps the records alone do not carry.
        Discovered: DiscoveredSet
    }

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

    /// Discovers the connected set for `rootAccession` and logs the per-entity
    /// counts. Shared by `crawlCore` (typed-record fetch) and `crawlToXml`
    /// (raw-XML file writer) so both paths get the same discovery log.
    let discoverAsync (options: CrawlOptions) (rootAccession: string) : Async<DiscoveredSet> =
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

            return discovered
        }

    /// Discovers the connected set for `rootAccession`, then fetches + parses
    /// every entity kind. Returns the records and the discovery set (the latter
    /// carries the relationships persistence needs to thread foreign keys).
    let crawlCore (options: CrawlOptions) (rootAccession: string) : Async<CrawlResult * DiscoveredSet> =
        async {
            let! discovered = discoverAsync options rootAccession

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

    open BioFSharp.IO.INSDC
    open System.IO

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

    // ---- Resolve: an INSDC accession -> the ids the other services key on ----
    //
    // The middle layer. EuropePMC keys on a PMCID and DEE2 keys on an SRA study
    // accession; only the INSDC records know either. Exposing that hop as ordinary
    // functions is what lets a caller compose the pieces freely — fetch a paper
    // from a PMCID they already hold, or discover it from an accession; download a
    // DEE2 bundle from an SRP they already hold, or resolve one from an accession —
    // instead of being limited to whichever combinations an orchestrator happens to
    // have baked in.

    /// The refs carried by records you have already fetched — so `paperRefs` and
    /// `dee2Key` work identically whether you took the lean `resolve` path or a full
    /// `crawl` / `crawlAndDiscover`.
    let refsOf (accession: string) (result: CrawlResult) (discovered: DiscoveredSet) : InsdcRefs =
        { Accession = accession
          BioProjects = result.BioProjects
          Studies = result.Studies
          Discovered = discovered }

    /// Resolves `accession` to just the records the downstream lookups read — the
    /// BioProject and the Study.
    ///
    /// Deliberately does **not** fetch the BioSample/Experiment/Run records: neither
    /// a paper lookup nor a DEE2 lookup ever reads one, and on a large project they
    /// are thousands of wasted requests. Use `crawlAndDiscover` when you genuinely
    /// need the full record set (as the R2 XML tree does).
    let resolveWithAsync (options: CrawlOptions) (accession: string) : Async<InsdcRefs> =
        async {
            let! discovered = Fetch.discoverAsync options accession
            let! bioProjects = Fetch.entity options "BioProject" BioProject.readString discovered.BioProjects
            let! studies = Fetch.entity options "Study" Study.readString discovered.Studies

            return
                { Accession = accession
                  BioProjects = bioProjects
                  Studies = studies
                  Discovered = discovered }
        }

    /// `resolveWithAsync` with `CrawlOptions.Default`.
    let resolveAsync (accession: string) : Async<InsdcRefs> =
        resolveWithAsync CrawlOptions.Default accession

    /// Blocking `resolveAsync` — the `accession -> InsdcRefs` surface.
    let resolve (accession: string) : InsdcRefs =
        resolveAsync accession |> Async.RunSynchronously

    /// The publication cross-references the resolved records carry — the INSDC →
    /// EuropePMC hop, as a pure projection. Feed the result to `Paper.resolvePmcid`
    /// to get the PMCID the full-text endpoints are keyed on. A record with no
    /// `LINKS` section (entirely normal) contributes nothing rather than throwing.
    let paperRefs (refs: InsdcRefs) : PublicationRef list =
        let links =
            [ for project in refs.BioProjects do
                  if not (isNull (box project.ProjectLinks)) then
                      yield! project.ProjectLinks
              for study in refs.Studies do
                  if not (isNull (box study.StudyLinks)) then
                      yield! study.StudyLinks ]

        Paper.publicationRefs links

    /// The SRA study accession DEE2 keys its bundles on — the INSDC → DEE2 hop, as a
    /// pure projection.
    ///
    /// This is the archive-assigned `Accession` (SRP/ERP/DRP), **not** the submitter
    /// `Alias`: a GEO-origin study's alias is its GEO series id (e.g. `GSE125950`),
    /// which DEE2 never keys on, while the accession (e.g. `SRP183179`) is what its
    /// `search2.sh` resolves. `None` when no Study was discovered — a project with no
    /// study has no bundle to find.
    ///
    /// A project with several studies keys on the first; whole-project DEE2 (one
    /// bundle per study) is a noted future extension.
    let dee2Key (refs: InsdcRefs) : string option =
        refs.Studies |> Array.tryHead |> Option.map (fun study -> study.Accession)

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

    // ---- R2 raw-XML file writer ----

    /// The on-disk folder (relative to `outDir`) where one entity kind's
    /// per-accession XML files are written. `BioProject` and `Study` live at
    /// the root (the empty string) — they are the project-level containers;
    /// `BioSample`/`Experiment`/`Run` live in named subfolders.
    let private folderFor (kind: string) : string =
        match kind with
        | "BioProject"
        | "Study" -> ""
        | "BioSample" -> "samples"
        | "Experiment" -> "experiments"
        | "Run" -> "runs"
        | _ -> ""

    /// Writes `records` to `<outDir>/<folder>/<accession>.xml` via the IO
    /// `write` function, skipping files that already exist (idempotent
    /// resume). Returns `(written, skipped)`.
    let private writeRecords
        (outDir: string)
        (kind: string)
        (accessionOf: 'T -> string)
        (writeOne: string -> 'T -> unit)
        (records: 'T[])
        : int * int =
        let dir = Path.Combine(outDir, folderFor kind)
        Directory.CreateDirectory dir |> ignore

        let mutable written = 0
        let mutable skipped = 0

        for record in records do
            let path = Path.Combine(dir, accessionOf record + ".xml")

            if File.Exists path then
                skipped <- skipped + 1
            else
                writeOne path record
                written <- written + 1

        written, skipped

    /// Discovers, fetches, parses, and writes every record connected to
    /// `accession` as per-accession XML files in a project-shaped tree under
    /// `outDir`, using the `BioFSharp.IO.INSDC` writers. Returns the typed
    /// `CrawlResult` alongside the `DiscoveredSet` so the `crawlAll`
    /// orchestrator can read the Study records' `Alias` for the DEE2 step
    /// without a second discovery call.
    let private crawlToXmlCore (options: CrawlOptions) (accession: string) (outDir: string) : Async<CrawlResult * DiscoveredSet> =
        async {
            let! result, discovered = Fetch.crawlCore options accession

            let pW, pS =
                writeRecords outDir "BioProject" (fun (r: BioProject) -> r.Accession) BioProject.write result.BioProjects

            options.Log(WritingXml(pW, pS, "BioProject"))

            let sW, sS =
                writeRecords outDir "Study" (fun (r: Study) -> r.Accession) Study.write result.Studies

            options.Log(WritingXml(sW, sS, "Study"))

            let smW, smS =
                writeRecords outDir "BioSample" (fun (r: BioSample) -> r.Accession) BioSample.write result.BioSamples

            options.Log(WritingXml(smW, smS, "BioSample"))

            let eW, eS =
                writeRecords outDir "Experiment" (fun (r: Experiment) -> r.Accession) Experiment.write result.Experiments

            options.Log(WritingXml(eW, eS, "Experiment"))

            let rW, rS =
                writeRecords outDir "Run" (fun (r: Run) -> r.Accession) Run.write result.Runs

            options.Log(WritingXml(rW, rS, "Run"))

            return result, discovered
        }

    /// Crawls every record connected to `accession` and writes the INSDC
    /// XML to per-accession files in a project-shaped tree under `outDir`:
    ///
    ///   BioProject and Study at the root; `samples/`, `experiments/`,
    ///   `runs/` subfolders for the rest. Idempotent: files already on disk
    ///   are skipped (resume). Records are round-tripped through the
    ///   `BioFSharp.IO.INSDC` readers + writers (the same path the roundtrip
    ///   tests exercise), so the output is guaranteed to re-parse.
    let crawlToXmlWithAsync (options: CrawlOptions) (accession: string) (outDir: string) : Async<unit> =
        async {
            let! _ = crawlToXmlCore options accession outDir
            options.Log(Completed(sprintf "%s -> xml tree at %s" accession outDir))
        }

    /// `crawlToXmlWithAsync` with `CrawlOptions.Default`.
    let crawlToXmlAsync (accession: string) (outDir: string) : Async<unit> =
        crawlToXmlWithAsync CrawlOptions.Default accession outDir

    /// Blocking `crawlToXmlAsync` — the `accession -> outDir -> ()` surface.
    let crawlToXml (accession: string) (outDir: string) : unit =
        crawlToXmlAsync accession outDir |> Async.RunSynchronously

    // ---- Orchestrator inputs: where each artifact comes from ----

    /// Where an orchestrated crawl's paper comes from.
    ///
    /// An explicit DU rather than a `string option`, in which `None` had to mean
    /// "auto-discover" — the opposite sense to `dee2Species = None`, which meant
    /// "skip" — and which left no way at all to say *fetch no paper*.
    type PaperSource =
        /// Fetch exactly this PMCID: you already have the id.
        | PaperFrom of pmcid: string
        /// Discover the paper from the crawled records' own PUBMED/PMC xrefs
        /// (`paperRefs` → `Paper.resolvePmcid`).
        | PaperDiscover
        /// Fetch no paper at all.
        | PaperSkip

    /// Where an orchestrated crawl's DEE2 count archive comes from.
    type Dee2Source =
        /// Look the bundle up for `species` using a study accession you already have.
        | Dee2From of species: string * studyAccession: string
        /// Look it up for `species`, keyed on the study accession resolved from the
        /// INSDC records (`dee2Key`).
        | Dee2Discover of species: string
        /// Fetch no DEE2 bundle at all.
        | Dee2Skip

    /// Resolves the PMCID a `PaperSource` designates, **fetching no full text**.
    /// Shared by both orchestrators.
    let private paperTargetWithAsync
        (options: CrawlOptions)
        (refs: InsdcRefs)
        (source: PaperSource)
        : Async<string option>
        =
        async {
            match source with
            | PaperSkip -> return None
            | PaperFrom pmcid -> return Some pmcid
            | PaperDiscover -> return! Paper.resolvePmcidWithAsync options (paperRefs refs)
        }

    /// Fetches the DEE2 bundle a `Dee2Source` designates into `outDir`. Shared by
    /// both orchestrators.
    let private fetchDee2WithAsync
        (options: CrawlOptions)
        (refs: InsdcRefs)
        (source: Dee2Source)
        (outDir: string)
        : Async<string option>
        =
        async {
            match source with
            | Dee2Skip -> return None
            | Dee2From (species, studyAccession) ->
                options.Log(Fetching("dee2", 1))
                return! Dee2.crawlDee2WithAsync options species studyAccession outDir
            | Dee2Discover species ->
                match dee2Key refs with
                | Some studyAccession ->
                    options.Log(Fetching("dee2", 1))
                    return! Dee2.crawlDee2WithAsync options species studyAccession outDir
                | None ->
                    options.Log(BundleNotFound(species, "<none>"))
                    return None
        }

    // ---- R2 orchestrator (INSDC XML + paper + DEE2) ----

    /// Per-folder file counts from `crawlAll`, so the caller can verify what
    /// landed. `"root"` counts BioProject + Study files (both at root level);
    /// the rest are their named subfolders.
    type CrawlSummary =
        {
            /// The `outDir` the crawl wrote to.
            ProjectDir: string
            /// Per-folder INSDC XML file counts: `"root"`, `"samples"`,
            /// `"experiments"`, `"runs"`.
            InsdcCounts: Map<string, int>
            /// The paper crawl result. `JatsXml`/`Pdf` carry the written path;
            /// `NotFound` means no paper file was written.
            Paper: PaperResult
            /// The DEE2 bundle zip path, or `None` if no bundle matched / the
            /// step was skipped.
            Dee2Path: string option
        }

    /// Counts the INSDC XML files per folder under `outDir`, for `CrawlSummary`.
    let private countInsdc (outDir: string) : Map<string, int> =
        let count folder =
            let dir = Path.Combine(outDir, folder)

            if Directory.Exists dir then
                Directory.GetFiles(dir, "*.xml").Length
            else
                0

        // Root-level: BioProject + Study files (*.xml directly under outDir).
        let rootCount =
            if Directory.Exists outDir then
                Directory.GetFiles(outDir, "*.xml").Length
            else
                0

        Map.ofList [ "root", rootCount; "samples", count "samples"; "experiments", count "experiments"; "runs", count "runs" ]

    /// Crawls the **R2** artifacts for one project accession into one `outDir` tree:
    /// the INSDC XML record tree, the paper, and the DEE2 count archive.
    ///
    /// A thin composition over the public pieces — `crawlToXml` for the tree,
    /// `refsOf` + `paperRefs`/`dee2Key` for the resolution hops, and `Paper`/`Dee2`
    /// for the fetches. Reach for those directly whenever you want a combination
    /// this convenience does not offer.
    ///
    /// `paper` and `dee2` say where each artifact comes from — an id you already
    /// hold, discovery from the crawled records, or not at all. R2's paper takes
    /// whichever full-text format is available (JATS, else PDF); R1 is the one that
    /// pins a specific format.
    let crawlAllWithAsync
        (options: CrawlOptions)
        (accession: string)
        (outDir: string)
        (paper: PaperSource)
        (dee2: Dee2Source)
        : Async<CrawlSummary>
        =
        async {
            // R2 writes the full XML tree, so this is the full record crawl, not the
            // lean `resolve`. `refsOf` then presents those records to the very same
            // resolution rules the lean path uses.
            let! result, discovered = crawlToXmlCore options accession outDir
            let insdcCounts = countInsdc outDir
            let refs = refsOf accession result discovered

            let! pmcid = paperTargetWithAsync options refs paper

            let! paperResult =
                match pmcid with
                | Some id ->
                    options.Log(Fetching("paper", 1))
                    Paper.crawlPaperWithAsync options id outDir
                | None -> async { return PaperResult.NotFound }

            let! dee2Path = fetchDee2WithAsync options refs dee2 outDir

            let summary =
                { ProjectDir = outDir
                  InsdcCounts = insdcCounts
                  Paper = paperResult
                  Dee2Path = dee2Path }

            options.Log(
                Completed(
                    sprintf
                        "%s -> %s (insdc=%A, paper=%A, dee2=%A)"
                        accession
                        outDir
                        summary.InsdcCounts
                        summary.Paper
                        summary.Dee2Path
                )
            )

            return summary
        }

    /// `crawlAllWithAsync` with `CrawlOptions.Default`.
    let crawlAllAsync
        (accession: string)
        (outDir: string)
        (paper: PaperSource)
        (dee2: Dee2Source)
        : Async<CrawlSummary>
        =
        crawlAllWithAsync CrawlOptions.Default accession outDir paper dee2

    /// Blocking `crawlAllAsync` — the unified `accession -> outDir -> PaperSource ->
    /// Dee2Source -> CrawlSummary` surface.
    let crawlAll (accession: string) (outDir: string) (paper: PaperSource) (dee2: Dee2Source) : CrawlSummary =
        crawlAllAsync accession outDir paper dee2 |> Async.RunSynchronously

    // ---- R1 orchestrator (DEE2 archive + paper, no INSDC XML) ----

    /// Which R1 AI readiness level format to materialize. The two differ *only*
    /// in the paper's file format — the DEE2 archive is byte-identical in both.
    type R1Format =
        /// DEE2 archive + the paper as JATS XML.
        | R1A
        /// DEE2 archive + the paper as PDF.
        | R1B

    /// What landed from an R1 crawl.
    type R1Summary =
        {
            /// The `outDir` the crawl wrote to.
            ProjectDir: string
            /// Which R1 readiness format was crawled.
            Format: R1Format
            /// The paper crawl result — `JatsXml` for `R1A`, `Pdf` for `R1B`, or
            /// `NotFound` when that format's full text was unavailable.
            Paper: PaperResult
            /// The DEE2 bundle zip path, or `None` when the study has no bundle
            /// or the step was skipped.
            Dee2Path: string option
        }

    /// The EuropePMC full-text format each R1 readiness format ships.
    let private paperFormatFor (format: R1Format) : PaperFormat =
        match format with
        | R1A -> PaperFormat.Jats
        | R1B -> PaperFormat.Pdf

    /// Crawls the R1 artifacts for one project accession into `outDir`, once, for
    /// every readiness format in `formats`:
    ///
    ///     `<outDir>/counts/<StudyAccession>.zip`   the DEE2 archive — SHARED
    ///     `<outDir>/paper/<PMCID>.jats.xml`        R1A
    ///     `<outDir>/paper/<PMCID>.pdf`             R1B
    ///
    /// **Everything except the paper is fetched exactly once and shared.** R1A and
    /// R1B differ *only* in the paper's file format, so ENA discovery, the
    /// BioProject/Study records, the PMCID resolution and the DEE2 archive download
    /// all happen a single time however many formats are asked for — only the paper
    /// full text is fetched per format. Crawling `[R1A; R1B]` therefore costs **one
    /// extra HTTP request** over crawling either alone, not a second full crawl.
    /// Prefer this over calling `crawlR1WithAsync` twice.
    ///
    /// Returns one `R1Summary` per requested format, in order: they share
    /// `ProjectDir` and `Dee2Path`, and differ in `Format`/`Paper`. Both formats'
    /// papers land in the same `<outDir>`, so a caller materializing them as two
    /// separate trees copies `counts/` plus its own paper file out of this one
    /// directory (see `playground/crawl_r1.fsx`).
    ///
    /// **No INSDC XML is written.** R1 deliberately obscures its source, so unlike
    /// `crawlAll` the record tree never lands on disk. Records are still *fetched*,
    /// but only the BioProject and Study — all R1 needs, since their publication
    /// xrefs drive paper discovery and the Study accession is the DEE2 lookup key.
    /// The BioSample/Experiment/Run records `crawlAll` fetches are skipped entirely,
    /// so a large project costs a handful of requests instead of thousands.
    ///
    /// `paperId = Some pmcid` fetches that paper directly; `None` auto-discovers it
    /// from the crawled records' own PUBMED/PMC xrefs. `dee2Species = None` skips
    /// the DEE2 step. Each paper is fetched in its requested format only — never
    /// falling back to the other, which would silently turn an `R1A` into an `R1B`.
    /// The artifacts fail independently: a missing paper does not stop the archive
    /// landing, and vice versa.
    let crawlR1FormatsWithAsync
        (options: CrawlOptions)
        (formats: R1Format list)
        (accession: string)
        (outDir: string)
        (paper: PaperSource)
        (dee2: Dee2Source)
        : Async<R1Summary list>
        =
        async {
            // Lean: R1 writes no XML, so it needs only the BioProject + Study, not
            // the thousands of BioSample/Experiment/Run records `crawlAll` fetches.
            let! refs = resolveWithAsync options accession

            // Resolved and fetched ONCE, shared by every format: the formats want
            // different *renditions* of the same article, and the very same archive.
            let! pmcid = paperTargetWithAsync options refs paper
            let! dee2Path = fetchDee2WithAsync options refs dee2 outDir

            // The one genuinely per-format step.
            let papers = ResizeArray<R1Format * PaperResult>()

            for format in formats do
                let! rendition =
                    match pmcid with
                    | Some id ->
                        options.Log(Fetching("paper", 1))
                        Paper.crawlPaperFormatWithAsync options (paperFormatFor format) id outDir
                    | None -> async { return PaperResult.NotFound }

                papers.Add(format, rendition)

            let summaries =
                papers
                |> Seq.map (fun (format, rendition) ->
                    { ProjectDir = outDir
                      Format = format
                      Paper = rendition
                      Dee2Path = dee2Path })
                |> List.ofSeq

            let perFormat =
                summaries
                |> List.map (fun s -> sprintf "%A: paper=%A" s.Format s.Paper)
                |> String.concat ", "

            options.Log(Completed(sprintf "%s -> %s (dee2=%A; %s)" accession outDir dee2Path perFormat))

            return summaries
        }

    /// `crawlR1FormatsWithAsync` with `CrawlOptions.Default`.
    let crawlR1FormatsAsync
        (formats: R1Format list)
        (accession: string)
        (outDir: string)
        (paper: PaperSource)
        (dee2: Dee2Source)
        : Async<R1Summary list>
        =
        crawlR1FormatsWithAsync CrawlOptions.Default formats accession outDir paper dee2

    /// Blocking `crawlR1FormatsAsync` — the `formats -> accession -> outDir ->
    /// PaperSource -> Dee2Source -> R1Summary list` surface.
    let crawlR1Formats
        (formats: R1Format list)
        (accession: string)
        (outDir: string)
        (paper: PaperSource)
        (dee2: Dee2Source)
        : R1Summary list
        =
        crawlR1FormatsAsync formats accession outDir paper dee2 |> Async.RunSynchronously

    /// Crawls a single R1 readiness format — `crawlR1FormatsWithAsync` for one
    /// format. **To materialize both R1A and R1B, use the `Formats` variants**:
    /// calling this twice re-runs ENA discovery and re-downloads the DEE2 archive,
    /// which crawling them together avoids (only the paper actually differs).
    let crawlR1WithAsync
        (options: CrawlOptions)
        (format: R1Format)
        (accession: string)
        (outDir: string)
        (paper: PaperSource)
        (dee2: Dee2Source)
        : Async<R1Summary>
        =
        async {
            let! summaries = crawlR1FormatsWithAsync options [ format ] accession outDir paper dee2
            return List.exactlyOne summaries
        }

    /// `crawlR1WithAsync` with `CrawlOptions.Default`.
    let crawlR1Async
        (format: R1Format)
        (accession: string)
        (outDir: string)
        (paper: PaperSource)
        (dee2: Dee2Source)
        : Async<R1Summary>
        =
        crawlR1WithAsync CrawlOptions.Default format accession outDir paper dee2

    /// Blocking `crawlR1Async` — the `format -> accession -> outDir -> PaperSource ->
    /// Dee2Source -> R1Summary` surface.
    let crawlR1
        (format: R1Format)
        (accession: string)
        (outDir: string)
        (paper: PaperSource)
        (dee2: Dee2Source)
        : R1Summary
        =
        crawlR1Async format accession outDir paper dee2 |> Async.RunSynchronously
