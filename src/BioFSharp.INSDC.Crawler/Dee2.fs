namespace BioFSharp.INSDC.Crawler

open System
open System.IO
open System.Text.RegularExpressions

/// DEE2 (Digital Expression Explorer 2) per-project bundle crawl. Resolves the
/// zipped count-matrix bundle for one SRA study accession (SRP/ERP/DRP) via the
/// `search2.sh` accession-search endpoint and writes it under
/// `<outDir>/counts/<accession>.zip`. Reuses `CrawlOptions` (its DEE2 search
/// base URL, fetch + retry, log sink) so the crawl integrates with the same
/// offline-testable seam as INSDC. The species is caller-supplied (e.g.
/// `"athaliana"`); the caller is responsible for the taxon → DEE2-species
/// mapping.
[<RequireQualifiedAccess>]
module Dee2 =

    /// The subfolder under `outDir` where DEE2 bundles are written.
    [<Literal>]
    let CountsFolder = "counts"

    /// A compiled regex capturing the bundle download URL (`…/<SRP>[_GSE…].zip`)
    /// out of a `search2.sh` result page. DEE2 emits the link UNQUOTED
    /// (`href=http://…zip`); the optional `"?` also tolerates a quoted variant.
    /// The `[^\s"'>]+` body stops at whitespace, a quote, or `>`.
    let private zipHrefRegex =
        Regex(@"href\s*=\s*""?(https?://[^\s""'>]+\.zip)", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

    /// Parses a DEE2 `search2.sh` result page into the bundle download URL.
    /// Returns `None` when the accession has no DEE2 data (the page reads "No
    /// results found") or carries no `.zip` link; otherwise the absolute `.zip`
    /// URL. Pure (no I/O) so the scrape is unit-testable in isolation.
    let parseSearchResult (html: string) : string option =
        if String.IsNullOrWhiteSpace html || html.Contains "No results found" then
            None
        else
            match zipHrefRegex.Match html with
            | m when m.Success -> Some m.Groups.[1].Value
            | _ -> None

    /// Resolves the DEE2 bundle download URL for one SRA study `accession` via
    /// the `search2.sh` endpoint. One HTTP GET; `None` when the accession has no
    /// bundle.
    let resolveBundleUrlWithAsync (options: CrawlOptions) (species: string) (accession: string) : Async<string option> =
        async {
            let url = Endpoints.dee2Search options.Dee2SearchBaseUrl species accession
            let fetch = Internal.Http.withRetry options.Retries options.Log options.Fetch
            let! html = fetch url
            return parseSearchResult html
        }

    /// `resolveBundleUrlWithAsync` with `CrawlOptions.Default`.
    let resolveBundleUrlAsync (species: string) (accession: string) : Async<string option> =
        resolveBundleUrlWithAsync CrawlOptions.Default species accession

    /// Blocking `resolveBundleUrlAsync`.
    let resolveBundleUrl (species: string) (accession: string) : string option =
        resolveBundleUrlAsync species accession |> Async.RunSynchronously

    /// Downloads the DEE2 project bundle for SRA study `accession` — resolved
    /// via `search2.sh` (`resolveBundleUrlWithAsync`) — and writes it to
    /// `<outDir>/counts/<accession>.zip`. Returns the written path, or `None`
    /// when the accession has no DEE2 bundle (logged as `BundleNotFound`). The
    /// zip is written atomically: it downloads to a temp path and moves into
    /// place on success, so a partial download never appears under the final
    /// name.
    let crawlDee2WithAsync
        (options: CrawlOptions)
        (species: string)
        (accession: string)
        (outDir: string)
        : Async<string option>
        =
        async {
            if String.IsNullOrWhiteSpace accession then
                options.Log(BundleNotFound(species, "<none>"))
                return None
            else
                let! bundleUrl = resolveBundleUrlWithAsync options species accession

                match bundleUrl with
                | None ->
                    options.Log(BundleNotFound(species, accession))
                    return None
                | Some bundleUrl ->
                    let fetchBytes = Internal.Http.withRetry options.Retries options.Log options.FetchBytes

                    try
                        let! bytes = fetchBytes bundleUrl
                        let dir = Path.Combine(outDir, CountsFolder)
                        Directory.CreateDirectory dir |> ignore

                        let finalPath = Path.Combine(dir, accession + ".zip")
                        let tempPath = Path.Combine(dir, accession + ".zip.tmp")

                        File.WriteAllBytes(tempPath, bytes)
                        File.Delete(finalPath)
                        File.Move(tempPath, finalPath)

                        options.Log(FetchedBundle(accession, finalPath))
                        return Some finalPath
                    with ex ->
                        options.Log(Failed(sprintf "fetch dee2 %s/%s" species accession, ex.Message))
                        return None
        }

    /// `crawlDee2WithAsync` with `CrawlOptions.Default`.
    let crawlDee2Async (species: string) (accession: string) (outDir: string) : Async<string option> =
        crawlDee2WithAsync CrawlOptions.Default species accession outDir

    /// Blocking `crawlDee2Async` — the `species -> accession -> outDir -> string option`
    /// surface.
    let crawlDee2 (species: string) (accession: string) (outDir: string) : string option =
        crawlDee2Async species accession outDir |> Async.RunSynchronously
