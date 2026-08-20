namespace BioFSharp.INSDC.Crawler

open System

/// A structured progress event emitted during a crawl. Consumers receive these
/// through `CrawlOptions.Log`; the built-in `Log.console` sink renders them to
/// stdout, and a custom sink can forward them to any logger (e.g. an `ILogger`).
type CrawlEvent =
    /// The crawl is starting — carries the root accession it was invoked with.
    /// Emitted before discovery so the very first log line identifies the crawl.
    | Started of accession: string
    /// Discovery finished — a per-entity count of distinct accessions found.
    | Discovered of counts: Map<string, int>
    /// About to fetch `count` records of the named entity kind.
    | Fetching of kind: string * count: int
    /// Parsed `count` records of the named entity kind from the fetched XML.
    | Parsed of kind: string * count: int
    /// A transient HTTP failure is being retried (1-based `attempt`).
    | Retrying of url: string * attempt: int * error: string
    /// Persisted an entity kind to SQLite: how many rows were inserted vs.
    /// skipped as already present.
    | Persisted of kind: string * inserted: int * skipped: int
    /// Wrote per-accession INSDC XML files for one entity kind: how many files
    /// were freshly written vs. skipped as already present. `kind` is the
    /// entity kind (`BioProject`, `Run`, ...); the on-disk folder is implied
    /// by the kind (see `XmlSave.folderFor`).
    | WritingXml of written: int * skipped: int * kind: string
    /// A publication cross-reference was auto-discovered on the crawled records
    /// (no caller-supplied paper id): the `ref` found (e.g. `"PUBMED:18808718"`)
    /// and the PMCID it resolved to via EuropePMC (`None` if it did not resolve
    /// to a PMC full-text id).
    | DiscoveredPaperRef of ref: string * pmcid: string option
    /// A paper was fetched from EuropePMC. `format` is `"jats"` or `"pdf"`
    /// (the PDF fallback); `path` is the written file path.
    | FetchedPaperFormat of id: string * format: string * path: string
    /// Both the JATS XML and the PDF fallback failed for a paper id; no
    /// paper file was written. Distinct from `Failed` so callers can
    /// distinguish "no paper available" from a transient crawl failure.
    | FetchPaperFailed of id: string * error: string
    /// A DEE2 project bundle was downloaded and written to `path`.
    | FetchedBundle of srp: string * path: string
    /// No DEE2 bundle for the SRP accession was found under the requested
    /// species listing; no file was written.
    | BundleNotFound of species: string * srp: string
    /// A non-fatal failure (a bad batch or an unlinkable record); the crawl
    /// continues.
    | Failed of context: string * error: string
    /// The crawl finished — a one-line human-readable summary.
    | Completed of summary: string

/// Built-in logging sinks for `CrawlEvent`s.
module Log =

    /// Renders `event` as a single human-readable line (no timestamp/prefix).
    let format (event: CrawlEvent) : string =
        match event with
        | Started accession -> sprintf "start — %s" accession
        | Discovered counts ->
            let parts =
                counts
                |> Map.toList
                |> List.map (fun (k, v) -> sprintf "%s=%d" k v)
                |> String.concat " "
            sprintf "discovered %s" parts
        | Fetching (kind, count) -> sprintf "fetching %d %s record(s)" count kind
        | Parsed (kind, count) -> sprintf "parsed %d %s record(s)" count kind
        | Retrying (url, attempt, error) -> sprintf "retry #%d %s (%s)" attempt url error
        | Persisted (kind, inserted, skipped) ->
            sprintf "persisted %s: %d inserted, %d skipped" kind inserted skipped
        | WritingXml (written, skipped, kind) ->
            sprintf "wrote %d %s xml (%d skipped)" written kind skipped
        | DiscoveredPaperRef (ref, pmcid) ->
            match pmcid with
            | Some p -> sprintf "discovered paper %s -> %s" ref p
            | None -> sprintf "discovered paper %s (unresolved)" ref
        | FetchedPaperFormat (id, fmt, path) -> sprintf "paper %s -> %s (%s)" id fmt path
        | FetchPaperFailed (id, error) -> sprintf "paper %s FAILED: %s" id error
        | FetchedBundle (srp, path) -> sprintf "dee2 %s -> %s" srp path
        | BundleNotFound (species, srp) -> sprintf "dee2 %s/%s bundle not found" species srp
        | Failed (context, error) -> sprintf "FAILED %s: %s" context error
        | Completed summary -> sprintf "done — %s" summary

    /// A sink that writes every event to stdout, timestamped and prefixed. This
    /// is the built-in default (`CrawlOptions.Default.Log`).
    let console (event: CrawlEvent) : unit =
        printfn "[crawler %s] %s" (DateTime.Now.ToString("HH:mm:ss")) (format event)

    let file (path: string) : CrawlEvent -> unit =
        let writer = new IO.StreamWriter(path, append = true)
        fun event ->
            writer.WriteLine(sprintf "[crawler %s] %s" (DateTime.Now.ToString("HH:mm:ss")) (format event))
            writer.Flush()

    /// A sink that discards every event — use in tests or when the caller wires
    /// its own logging.
    let silent (_: CrawlEvent) : unit = ()
