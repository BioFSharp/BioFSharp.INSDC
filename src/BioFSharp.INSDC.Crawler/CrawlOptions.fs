namespace BioFSharp.INSDC.Crawler

/// Tunable knobs for a crawl. `CrawlOptions.Default` supplies production
/// defaults (live ENA endpoints, the real FsHttp fetch, console logging); tests
/// override `Fetch`/`Log`/base URLs to run fully offline against fixtures.
type CrawlOptions =
    {
        /// Base URL of the ENA Browser API XML endpoint (record fetch).
        BrowserBaseUrl: string
        /// Base URL of the ENA Portal API `filereport` endpoint (discovery).
        PortalBaseUrl: string
        /// Maximum number of HTTP requests in flight at once.
        MaxConcurrency: int
        /// How many times to retry a transient HTTP failure before giving up.
        Retries: int
        /// Delay applied before each request, in milliseconds (0 disables it).
        /// A light throttle to stay polite to the ENA endpoints.
        ThrottleMs: int
        /// Maximum number of accessions batched into a single Browser API
        /// request (the endpoint returns a `*_SET` for comma-separated ids).
        ChunkSize: int
        /// The HTTP GET used to fetch a URL's body. Injectable so tests can run
        /// without network access; defaults to `Internal.Http.get` (FsHttp).
        Fetch: string -> Async<string>
        /// Sink for progress events; defaults to `Log.console`.
        Log: CrawlEvent -> unit
    }

    /// Production defaults: live ENA endpoints, the FsHttp-backed fetch, and
    /// built-in console logging. Conservative concurrency/throttle to stay
    /// polite to the public ENA services.
    static member Default: CrawlOptions =
        {
            BrowserBaseUrl = Endpoints.DefaultBrowserBaseUrl
            PortalBaseUrl = Endpoints.DefaultPortalBaseUrl
            MaxConcurrency = 4
            Retries = 3
            ThrottleMs = 100
            ChunkSize = 100
            Fetch = Internal.Http.get
            Log = Log.console
        }
