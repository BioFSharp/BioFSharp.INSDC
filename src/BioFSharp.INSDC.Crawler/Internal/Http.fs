namespace BioFSharp.INSDC.Crawler.Internal

open FsHttp

open BioFSharp.INSDC.Crawler

/// The live HTTP layer, backed by FsHttp. Kept in `Internal` because callers
/// interact with it only through `CrawlOptions.Fetch` (which defaults to
/// `Http.get`) and the retry policy the crawler wraps around it.
module Http =

    /// Performs a single HTTP GET for `url` and returns the response body as
    /// text. Throws on a non-2xx status. This is the production
    /// `CrawlOptions.Fetch`; tests substitute their own function.
    let get (url: string) : Async<string> =
        async {
            let! response = http { GET url } |> Request.sendAsync
            let status = int response.statusCode

            if status < 200 || status >= 300 then
                failwithf "HTTP %d for %s" status url

            return! response |> Response.toTextAsync
        }

    /// Wraps `fetch` with up to `retries` re-attempts on any exception, using
    /// exponential backoff (100ms, 200ms, 400ms, ...). A `Retrying` event is
    /// emitted through `log` before each re-attempt; the final failure is
    /// rethrown for the caller to handle.
    let withRetry
        (retries: int)
        (log: CrawlEvent -> unit)
        (fetch: string -> Async<string>)
        (url: string)
        : Async<string> =
        let rec attempt (n: int) =
            async {
                try
                    return! fetch url
                with ex when n < retries ->
                    log (Retrying(url, n + 1, ex.Message))
                    do! Async.Sleep(100 * (pown 2 n))
                    return! attempt (n + 1)
            }

        attempt 0
