namespace BioFSharp.INSDC.Crawler

open System

/// A FASTQ file a run produced, as reported by the ENA Portal `read_run` fields
/// (`fastq_ftp` + the aligned `fastq_md5` / `fastq_bytes`). These files are the
/// run's actual output and are NOT present in the run XML.
type FastqFile =
    {
        /// The FTP path of the file (ENA `fastq_ftp`).
        Url: string
        /// The file's MD5 checksum (ENA `fastq_md5`), or "" if not reported.
        Md5: string
        /// The file's size in bytes as text (ENA `fastq_bytes`), or "" if absent.
        Bytes: string
    }

/// One row of the ENA Portal `read_run` report: a run and the accessions it is
/// connected to. `StudyAccession` is ENA's `secondary_study_accession` (the
/// SRP/ERP/DRP Study); `ProjectAccession` is ENA's `study_accession` (the
/// PRJ... BioProject).
type DiscoveryRow =
    {
        /// The run accession (SRR/ERR/DRR) — unique per report row.
        RunAccession: string
        /// The run's experiment (SRX/ERX/DRX).
        ExperimentAccession: string
        /// The run's sample (SAMN/SAMEA/SAMD).
        SampleAccession: string
        /// The Study (SRP/ERP/DRP).
        StudyAccession: string
        /// The BioProject (PRJ...).
        ProjectAccession: string
        /// The FASTQ files this run produced (may be empty).
        FastqFiles: FastqFile list
    }

/// The set of connected accessions discovered for a root accession, plus the
/// parent relationships needed to thread the SQLite foreign keys on insert.
type DiscoveredSet =
    {
        /// Every report row, verbatim (source of the connectivity graph).
        Rows: DiscoveryRow list
        /// Distinct BioProject accessions.
        BioProjects: string list
        /// Distinct Study accessions.
        Studies: string list
        /// Distinct BioSample accessions.
        BioSamples: string list
        /// Distinct Experiment accessions.
        Experiments: string list
        /// Distinct Run accessions.
        Runs: string list
        /// study accession -> its parent project accession.
        StudyToProject: Map<string, string>
        /// experiment accession -> its parent study accession.
        ExperimentToStudy: Map<string, string>
        /// run accession -> its parent experiment accession.
        RunToExperiment: Map<string, string>
    }

/// Discovery against the ENA Portal API: enumerate every record connected to a
/// root project/study accession from the run-level `filereport` table.
module Discovery =

    let private nonEmpty (s: string) = not (String.IsNullOrWhiteSpace s)

    let private distinctList (xs: string seq) =
        xs |> Seq.filter nonEmpty |> Seq.distinct |> List.ofSeq

    /// The empty discovered set (header-only or empty report).
    let empty: DiscoveredSet =
        {
            Rows = []
            BioProjects = []
            Studies = []
            BioSamples = []
            Experiments = []
            Runs = []
            StudyToProject = Map.empty
            ExperimentToStudy = Map.empty
            RunToExperiment = Map.empty
        }

    /// Parses the tab-separated `filereport` body into a `DiscoveredSet`. The
    /// header row is required and columns are located by name (order-independent).
    /// A header-only report is a valid childless project/study. Empty bodies,
    /// missing required columns, truncated rows, misaligned FASTQ metadata, and
    /// conflicting parent relationships raise `FormatException`.
    let parse (tsv: string) : DiscoveredSet =
        if isNull tsv then
            raise (FormatException("ENA filereport response is null."))

        let lines =
            tsv.Replace("\r\n", "\n").Split('\n')
            |> Array.filter nonEmpty

        match Array.toList lines with
        | [] -> raise (FormatException("ENA filereport response is empty."))
        | header :: rows ->
            let columns =
                header.TrimStart('\uFEFF').Split('\t')
                |> Array.map (fun c -> c.Trim())

            let requiredColumns =
                [ "run_accession"
                  "experiment_accession"
                  "sample_accession"
                  "study_accession"
                  "secondary_study_accession" ]

            let missing =
                requiredColumns
                |> List.filter (fun name -> columns |> Array.contains name |> not)

            if not (List.isEmpty missing) then
                raise (
                    FormatException(
                        sprintf "ENA filereport is missing required column(s): %s" (String.concat ", " missing)
                    )
                )

            let indexOf name = Array.findIndex ((=) name) columns
            let iRun = indexOf "run_accession"
            let iExperiment = indexOf "experiment_accession"
            let iSample = indexOf "sample_accession"
            let iProject = indexOf "study_accession"
            let iStudy = indexOf "secondary_study_accession"

            let cell (fields: string[]) i =
                if i < fields.Length then fields.[i].Trim() else ""

            // FASTQ columns are optional (a caller may request a narrower field set).
            let iFastqFtp = Array.tryFindIndex (fun c -> c = "fastq_ftp") columns
            let iFastqMd5 = Array.tryFindIndex (fun c -> c = "fastq_md5") columns
            let iFastqBytes = Array.tryFindIndex (fun c -> c = "fastq_bytes") columns

            // A semicolon-separated column split into aligned parts (empty when absent).
            let splitList (fields: string[]) (index: int option) =
                match index with
                | Some i -> (cell fields i).Split(';') |> Array.map (fun s -> s.Trim())
                | None -> [||]

            let fastqOf (fields: string[]) =
                let ftp = splitList fields iFastqFtp
                let md5 = splitList fields iFastqMd5
                let bytes = splitList fields iFastqBytes

                let validateAligned (name: string) (values: string[]) =
                    if values.Length > 0 && values.Length <> ftp.Length then
                        raise (
                            FormatException(
                                sprintf
                                    "ENA filereport FASTQ column '%s' has %d value(s), but fastq_ftp has %d."
                                    name
                                    values.Length
                                    ftp.Length
                            )
                        )

                validateAligned "fastq_md5" md5
                validateAligned "fastq_bytes" bytes

                ftp
                |> Array.mapi (fun i url ->
                    {
                        Url = url
                        Md5 = if i < md5.Length then md5.[i] else ""
                        Bytes = if i < bytes.Length then bytes.[i] else ""
                    })
                |> Array.filter (fun f -> nonEmpty f.Url)
                |> List.ofArray

            let highestRequiredIndex =
                [ iRun; iExperiment; iSample; iProject; iStudy ] |> List.max

            let parsedRows =
                rows
                |> List.mapi (fun rowIndex row ->
                    let fields = row.Split('\t')

                    if fields.Length <= highestRequiredIndex then
                        raise (
                            FormatException(
                                sprintf
                                    "ENA filereport row %d is truncated: expected at least %d columns, found %d."
                                    (rowIndex + 2)
                                    (highestRequiredIndex + 1)
                                    fields.Length
                            )
                        )

                    {
                        RunAccession = cell fields iRun
                        ExperimentAccession = cell fields iExperiment
                        SampleAccession = cell fields iSample
                        StudyAccession = cell fields iStudy
                        ProjectAccession = cell fields iProject
                        FastqFiles = fastqOf fields
                    })

            let mapOf relationship (key: DiscoveryRow -> string) (value: DiscoveryRow -> string) =
                let pairs =
                    parsedRows
                    |> List.choose (fun r ->
                        let k, v = key r, value r
                        if nonEmpty k && nonEmpty v then Some(k, v) else None)

                for keyValue, grouped in pairs |> List.groupBy fst do
                    let parents = grouped |> List.map snd |> List.distinct

                    if List.length parents > 1 then
                        raise (
                            FormatException(
                                sprintf
                                    "ENA filereport contains conflicting %s parents for '%s': %s"
                                    relationship
                                    keyValue
                                    (String.concat ", " parents)
                            )
                        )

                Map.ofList pairs

            {
                Rows = parsedRows
                BioProjects = parsedRows |> List.map (fun r -> r.ProjectAccession) |> distinctList
                Studies = parsedRows |> List.map (fun r -> r.StudyAccession) |> distinctList
                BioSamples = parsedRows |> List.map (fun r -> r.SampleAccession) |> distinctList
                Experiments = parsedRows |> List.map (fun r -> r.ExperimentAccession) |> distinctList
                Runs = parsedRows |> List.map (fun r -> r.RunAccession) |> distinctList
                StudyToProject = mapOf "study-to-project" (fun r -> r.StudyAccession) (fun r -> r.ProjectAccession)
                ExperimentToStudy = mapOf "experiment-to-study" (fun r -> r.ExperimentAccession) (fun r -> r.StudyAccession)
                RunToExperiment = mapOf "run-to-experiment" (fun r -> r.RunAccession) (fun r -> r.ExperimentAccession)
            }

    /// Ensures `rootAccession` is present in the discovered set even when the
    /// `read_run` report never named it. Discovery is entirely run-driven, so a
    /// project or study with no runs yields a header-only report and an empty
    /// set — without this the root's own record would never be fetched or
    /// stored. The root is added to the bucket its accession prefix implies
    /// (`PRJ...` -> BioProject, `SRP`/`ERP`/`DRP...` -> Study); an unrecognized
    /// prefix (or one already present) leaves the set untouched.
    let withRoot (rootAccession: string) (set: DiscoveredSet) : DiscoveredSet =
        let hasPrefix (prefixes: string list) =
            prefixes
            |> List.exists (fun p -> rootAccession.StartsWith(p, StringComparison.OrdinalIgnoreCase))

        let addDistinct (xs: string list) =
            if List.contains rootAccession xs then xs else xs @ [ rootAccession ]

        if String.IsNullOrWhiteSpace rootAccession then set
        elif hasPrefix [ "PRJ" ] then { set with BioProjects = addDistinct set.BioProjects }
        elif hasPrefix [ "SRP"; "ERP"; "DRP" ] then { set with Studies = addDistinct set.Studies }
        else set

    /// Fetches and parses the `filereport` for `rootAccession` using `options`
    /// (its Portal base URL, fetch function, retry count, and log sink), then
    /// seeds the root itself (`withRoot`) so a childless project/study still
    /// yields its own record.
    let discoverAsync (options: CrawlOptions) (rootAccession: string) : Async<DiscoveredSet> =
        async {
            let url = Endpoints.portalFileReport options.PortalBaseUrl rootAccession
            let! tsv = Internal.Http.withRetry options.Retries options.Log options.Fetch url
            return parse tsv |> withRoot rootAccession
        }
