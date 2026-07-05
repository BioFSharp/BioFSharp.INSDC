namespace BioFSharp.INSDC.Crawler

open System

/// A structured progress event emitted during a crawl. Consumers receive these
/// through `CrawlOptions.Log`; the built-in `Log.console` sink renders them to
/// stdout, and a custom sink can forward them to any logger (e.g. an `ILogger`).
type CrawlEvent =
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
        | Failed (context, error) -> sprintf "FAILED %s: %s" context error
        | Completed summary -> sprintf "done — %s" summary

    /// A sink that writes every event to stdout, timestamped and prefixed. This
    /// is the built-in default (`CrawlOptions.Default.Log`).
    let console (event: CrawlEvent) : unit =
        printfn "[crawler %s] %s" (DateTime.Now.ToString("HH:mm:ss")) (format event)

    /// A sink that discards every event — use in tests or when the caller wires
    /// its own logging.
    let silent (_: CrawlEvent) : unit = ()
