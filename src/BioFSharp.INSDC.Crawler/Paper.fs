namespace BioFSharp.INSDC.Crawler

open System
open System.IO
open System.Text.Json

open BioFSharp.FileFormats.INSDC

/// The result of a paper crawl — which full-text format (if any) landed on
/// disk. `JatsXml` is the only format `IngestReaders.readJats` can parse
/// today; `Pdf` is a raw artifact for the on-disk tree (and future text
/// extraction); `NotFound` means both the JATS XML and the PDF fallback
/// failed — no file was written.
type PaperResult =
    /// The JATS XML full text was fetched and written to `path`.
    | JatsXml of path: string
    /// No open-access JATS XML was available, but the PDF full text was
    /// fetched and written to `path`.
    | Pdf of path: string
    /// Both the JATS XML and the PDF failed — no file was written.
    | NotFound

/// Which EuropePMC full-text format to fetch. `Paper.crawlPaperFormat` fetches
/// exactly the requested one and reports `NotFound` if it is unavailable;
/// `Paper.crawlPaper` keeps the JATS-then-PDF fallback. Qualified access is
/// required because `Pdf` would otherwise shadow `PaperResult.Pdf`.
[<RequireQualifiedAccess>]
type PaperFormat =
    /// The JATS XML full text (`fullTextXML`).
    | Jats
    /// The article PDF, from the PMC Open Access dataset (NOT EuropePMC — see
    /// `tryPdfAsync`).
    | Pdf

/// A publication cross-reference discovered on an INSDC record's own links —
/// the automatic alternative to a caller-supplied paper id. Submitters often
/// register the study's paper as an `XREF_LINK` on the BioProject/Study
/// (`<DB>PUBMED</DB>` or `<DB>PMC</DB>`). `Pubmed` carries a PMID, which must be
/// resolved to a PMCID before full text (EuropePMC's `fullTextXML` is keyed on
/// PMCID); `Pmc` carries a PMCID, ready to fetch directly.
type PublicationRef =
    /// A PubMed id (`XREF_LINK` with `DB = PUBMED`).
    | Pubmed of pmid: string
    /// A PMC id (`XREF_LINK` with `DB = PMC`), normalized to the `PMCXXXXX` form.
    | Pmc of pmcid: string

/// A EuropePMC article record — the subset of the `search` response the crawler
/// needs to fetch full text and label the artifact. `Pmcid` is present only
/// when EuropePMC holds the article in its PMC set (the id `fullTextXML`
/// requires); `IsOpenAccess` gates whether open-access JATS full text is
/// expected (versus the PDF fallback or metadata only).
type Article =
    {
        /// The article's PubMed id, if any.
        Pmid: string option
        /// The article's PMC id (`PMCXXXXX`) — the id the full-text endpoints
        /// are keyed on — if EuropePMC holds it in PMC.
        Pmcid: string option
        /// The article's DOI, if any.
        Doi: string option
        /// The article title, if present.
        Title: string option
        /// True when EuropePMC flags the article open access (`isOpenAccess = "Y"`).
        IsOpenAccess: bool
    }

/// EuropePMC full-text paper crawl. Tries the JATS XML endpoint first; on
/// any failure that indicates no open-access XML (4xx / empty body /
/// non-XML content), falls back to the PDF endpoint. Writes whichever
/// succeeds under `<outDir>/paper/<sanitized-id>.<ext>`. Reuses
/// `CrawlOptions` (its EuropePMC base URL, fetch + retry, log sink) so the
/// paper crawl integrates with the same offline-testable seam as INSDC.
[<RequireQualifiedAccess>]
module Paper =

    /// The subfolder under `outDir` where paper files are written.
    [<Literal>]
    let PaperFolder = "paper"

    /// Sanitizes a paper id (DOI / PMC / PMID) into a filesystem-safe
    /// filename stem: `/` (DOIs) → `_`, everything else left as-is
    /// (accessions and PMC ids are already `[A-Za-z0-9]`).
    let private sanitize (id: string) : string =
        id.Replace('/', '_')

    let private paperPath (outDir: string) (id: string) extension =
        Path.Combine(outDir, PaperFolder, sanitize id + extension)

    /// Writes `xml` to `<outDir>/paper/<sanitized>.jats.xml`, creating the
    /// folder if needed. Returns the written path.
    let private writeJats (outDir: string) (id: string) (xml: string) : string =
        let dir = Path.Combine(outDir, PaperFolder)
        Directory.CreateDirectory dir |> ignore

        let path = paperPath outDir id ".jats.xml"
        Internal.Files.writeText path xml
        path

    /// Writes `bytes` to `<outDir>/paper/<sanitized>.pdf`, creating the
    /// folder if needed. Returns the written path.
    let private writePdf (outDir: string) (id: string) (bytes: byte[]) : string =
        let dir = Path.Combine(outDir, PaperFolder)
        Directory.CreateDirectory dir |> ignore

        let path = paperPath outDir id ".pdf"
        Internal.Files.writeBytes path bytes
        path

    /// True if `xml` looks like a JATS / article XML body (starts with `<`
    /// and contains an `<article` element somewhere). EuropePMC's
    /// `fullTextXML` returns an empty body or an HTML error page when no
    /// open-access full text exists — this is the gate for the PDF fallback.
    let private looksLikeJats (xml: string) : bool =
        let trimmed = xml.TrimStart()
        not (String.IsNullOrWhiteSpace trimmed)
        && trimmed.StartsWith("<")
        && xml.Contains("<article")

    /// Checks the signature required at the start of a PDF file.
    let private looksLikePdf (bytes: byte[]) =
        bytes.Length >= 5
        && bytes.[0] = byte '%'
        && bytes.[1] = byte 'P'
        && bytes.[2] = byte 'D'
        && bytes.[3] = byte 'F'
        && bytes.[4] = byte '-'

    /// Runs `fetch url` inside `Async.Catch`, returning `Some x` on success
    /// or `None` on any exception. Stays properly asynchronous (unlike
    /// `try/with` inside an async workflow, `Async.Catch` does not break
    /// the workflow's type inference).
    let private tryFetchAsync (fetch: string -> Async<'T>) (url: string) : Async<'T option> =
        async {
            let! choice = Async.Catch(fetch url)

            match choice with
            | Choice1Of2 x -> return Some x
            | Choice2Of2 (:? OperationCanceledException as ex) -> return raise ex
            | Choice2Of2 _ -> return None
        }

    /// Normalizes a PMC id to the canonical `PMCXXXXX` form — some `XREF_LINK`s
    /// carry the bare digits, others the prefixed id. Case-insensitive on the
    /// existing prefix.
    let private normalizePmcId (id: string) : string =
        if id.StartsWith("PMC", StringComparison.OrdinalIgnoreCase) then
            "PMC" + id.Substring(3)
        else
            "PMC" + id

    /// Scans INSDC records' `Link` collections for publication cross-references,
    /// returning a `PublicationRef` for each `XREF_LINK` whose `DB` names a
    /// literature database — `PUBMED` → `Pubmed`, `PMC` → `Pmc` (id normalized
    /// to `PMCXXXXX`). The housekeeping xrefs every ENA record carries
    /// (`ENA-FASTQ-FILES`, `ENA-SUBMISSION`, ...) and URL/Entrez links are
    /// ignored. `links` may be null (no links section) — yields `[]`.
    let publicationRefs (links: seq<Link>) : PublicationRef list =
        if isNull links then
            []
        else
            [ for link in links do
                if not (isNull link) && not (isNull link.XrefLink) then
                    let x = link.XrefLink

                    if not (isNull x.Db) && not (isNull x.Id) then
                        match x.Db.Trim().ToUpperInvariant() with
                        | "PUBMED" -> yield Pubmed(x.Id.Trim())
                        | "PMC"
                        | "PMC-ARTICLE" -> yield Pmc(normalizePmcId (x.Id.Trim()))
                        | _ -> () ]

    /// Reads an optional string property off a JSON object element, mapping a
    /// missing property or a null/empty value to `None`.
    let private jsonStr (el: JsonElement) (name: string) : string option =
        match el.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String ->
            match v.GetString() with
            | null
            | "" -> None
            | s -> Some s
        | _ -> None

    /// Parses a EuropePMC `search` JSON body (`format=json`) into the `Article`
    /// hits it lists, in result order. Tolerant of missing fields (every modeled
    /// field is optional; `IsOpenAccess` defaults to `false`); an empty result
    /// list yields `[]`.
    let parseSearchResults (json: string) : Article list =
        use doc = JsonDocument.Parse(json)

        match doc.RootElement.TryGetProperty "resultList" with
        | true, resultList ->
            match resultList.TryGetProperty "result" with
            | true, results when results.ValueKind = JsonValueKind.Array ->
                [ for r in results.EnumerateArray() ->
                    { Pmid = jsonStr r "pmid"
                      Pmcid = jsonStr r "pmcid"
                      Doi = jsonStr r "doi"
                      Title = jsonStr r "title"
                      IsOpenAccess = (jsonStr r "isOpenAccess" = Some "Y") } ]
            | _ -> []
        | _ -> []

    /// Runs a EuropePMC `search` query through `options.Fetch` (with the shared
    /// retry policy) and parses the JSON into `Article` hits. `pageSize` caps
    /// the results requested.
    let searchAsync (options: CrawlOptions) (query: string) (pageSize: int) : Async<Article list> =
        async {
            let url = Endpoints.europePmcSearch options.EuropePmcBaseUrl query pageSize
            let fetch = Internal.Http.withRetry options.Retries options.Log options.Fetch
            let! json = fetch url
            return parseSearchResults json
        }

    /// Resolves a `PublicationRef` to a EuropePMC `Article` carrying the PMCID
    /// the full-text endpoints need. A `Pubmed` id is looked up by
    /// `EXT_ID:<pmid> AND SRC:MED`; a `Pmc` id by `PMCID:<pmcid>` (falling back
    /// to a minimal `Article` with just the known PMCID when the enrichment
    /// search turns up nothing). Any fetch/parse failure is swallowed to `None`
    /// — discovery is best-effort and must not abort a crawl. `None` means
    /// EuropePMC has no matching article, so no full text can be fetched.
    let resolveArticleAsync (options: CrawlOptions) (publication: PublicationRef) : Async<Article option> =
        async {
            let query =
                match publication with
                | Pubmed pmid -> sprintf "EXT_ID:%s AND SRC:MED" pmid
                | Pmc pmcid -> sprintf "PMCID:%s" pmcid

            let! outcome = Async.Catch(searchAsync options query 1)

            match outcome, publication with
            | Choice2Of2 (:? OperationCanceledException as ex), _ -> return raise ex
            | Choice1Of2 (article :: _), _ -> return Some article
            | _, Pmc pmcid ->
                // The PMCID is already in hand; a search miss/error must not lose it.
                return Some { Pmid = None; Pmcid = Some pmcid; Doi = None; Title = None; IsOpenAccess = false }
            | _, Pubmed _ -> return None
        }

    /// Fetches the JATS XML full text for `id` and writes it under
    /// `<outDir>/paper/`, returning the written path. `None` when EuropePMC has
    /// no open-access XML for the id — an HTTP error, an empty body, or a
    /// non-XML response (an HTML error page), all gated by `looksLikeJats`.
    /// Logs `FetchedPaperFormat` on success; whether a miss is fatal or falls
    /// back to the PDF is the caller's decision.
    let private tryJatsAsync (options: CrawlOptions) (id: string) (outDir: string) : Async<string option> =
        async {
            let existingPath = paperPath outDir id ".jats.xml"

            if File.Exists(existingPath) && looksLikeJats (File.ReadAllText(existingPath)) then
                options.Log(ReusedArtifact("paper/jats", existingPath))
                return Some existingPath
            else
                let url = Endpoints.europePmcFullTextXml options.EuropePmcBaseUrl id
                let fetch = Internal.Http.withRetry options.Retries options.Log options.Fetch
                let! jats = tryFetchAsync fetch url

                match jats with
                | Some xml when looksLikeJats xml ->
                    let path = writeJats outDir id xml
                    options.Log(FetchedPaperFormat(id, "jats", path))
                    return Some path
                | _ -> return None
        }

    /// The article versions tried, in order, when fetching a PDF from the PMC Open
    /// Access dataset. Articles are stored per version under a `PMC<id>.<v>/`
    /// prefix; `1` covers all but revised articles, and the ladder is only walked
    /// on a miss, so the common case still costs a single request.
    let PmcOaVersions = [ 1; 2; 3 ]

    /// Fetches the article PDF for `id` and writes it under `<outDir>/paper/`,
    /// returning the written path. `None` when no PDF is available.
    ///
    /// The PDF comes from the **PMC Open Access dataset on AWS**, not EuropePMC —
    /// the two full-text formats genuinely live in two different services. The
    /// EuropePMC `fullTextPDF` path this used to call **404s for every article**,
    /// including ones whose JATS serves fine and which EuropePMC flags open access,
    /// so `PaperResult.Pdf` was in practice unreachable in production while the
    /// tests passed happily on a stubbed byte array. See `Endpoints.pmcOaPdf`.
    let private tryPdfAsync (options: CrawlOptions) (id: string) (outDir: string) : Async<string option> =
        async {
            let existingPath = paperPath outDir id ".pdf"

            if File.Exists(existingPath) && looksLikePdf (File.ReadAllBytes(existingPath)) then
                options.Log(ReusedArtifact("paper/pdf", existingPath))
                return Some existingPath
            else
                let fetch = Internal.Http.withRetry options.Retries options.Log options.FetchBytes

                // Walk the version ladder, stopping at the first version that serves a
                // valid PDF signature. `tryFetchAsync` swallows the 404s in between.
                let rec attempt versions =
                    async {
                        match versions with
                        | [] -> return None
                        | version :: rest ->
                            let url = Endpoints.pmcOaPdf options.PmcOaBaseUrl id version
                            let! pdf = tryFetchAsync fetch url

                            match pdf with
                            | Some bytes when looksLikePdf bytes ->
                                let path = writePdf outDir id bytes
                                options.Log(FetchedPaperFormat(id, "pdf", path))
                                return Some path
                            | _ -> return! attempt rest
                    }

                return! attempt PmcOaVersions
        }

    /// Fetches the full text of paper `id` from EuropePMC in exactly `format` —
    /// **no fallback to the other format**. `NotFound` when that format is
    /// unavailable. This is what the R1 crawl needs: an R1A that silently fell
    /// back to a PDF would in fact be an R1B. Use `crawlPaperWithAsync` when
    /// either format will do. `id` must be a **PMCID** (`PMCXXXXX`) — both
    /// full-text endpoints are keyed on the PMC id, so a DOI or bare PMID
    /// returns 404; resolve a PMID to a PMCID first with `resolveArticleAsync`.
    let crawlPaperFormatWithAsync
        (options: CrawlOptions)
        (format: PaperFormat)
        (id: string)
        (outDir: string)
        : Async<PaperResult>
        =
        async {
            match format with
            | PaperFormat.Jats ->
                let! jats = tryJatsAsync options id outDir

                match jats with
                | Some path -> return JatsXml path
                | None ->
                    options.Log(FetchPaperFailed(id, "no JATS XML full text"))
                    return NotFound
            | PaperFormat.Pdf ->
                let! pdf = tryPdfAsync options id outDir

                match pdf with
                | Some path -> return Pdf path
                | None ->
                    options.Log(FetchPaperFailed(id, "no PDF full text"))
                    return NotFound
        }

    /// `crawlPaperFormatWithAsync` with `CrawlOptions.Default`.
    let crawlPaperFormatAsync (format: PaperFormat) (id: string) (outDir: string) : Async<PaperResult> =
        crawlPaperFormatWithAsync CrawlOptions.Default format id outDir

    /// Blocking `crawlPaperFormatAsync` — the `format -> id -> outDir -> PaperResult`
    /// surface.
    let crawlPaperFormat (format: PaperFormat) (id: string) (outDir: string) : PaperResult =
        crawlPaperFormatAsync format id outDir |> Async.RunSynchronously

    /// Fetches the full text of paper `id` from EuropePMC, taking whichever
    /// format is available. Tries the JATS XML endpoint first; if that fails
    /// (HTTP error, empty body, or non-XML response) it retries against the PDF
    /// endpoint before giving up. Writes whichever succeeds under
    /// `<outDir>/paper/`. `id` must be a **PMCID** (`PMCXXXXX`): both the
    /// EuropePMC `fullTextXML` and the PMC Open Access PDF store are both keyed on
    /// the PMC id — a DOI or bare PMID returns 404. Resolve a PMID to a PMCID first
    /// with `resolveArticleAsync`. Use `crawlPaperFormatWithAsync` when one specific
    /// format is required.
    let crawlPaperWithAsync (options: CrawlOptions) (id: string) (outDir: string) : Async<PaperResult> =
        async {
            let! jats = tryJatsAsync options id outDir

            match jats with
            | Some path -> return JatsXml path
            | None ->
                let! pdf = tryPdfAsync options id outDir

                match pdf with
                | Some path -> return Pdf path
                | None ->
                    options.Log(FetchPaperFailed(id, "both JATS XML and PDF failed"))
                    return NotFound
        }

    /// `crawlPaperWithAsync` with `CrawlOptions.Default`.
    let crawlPaperAsync (id: string) (outDir: string) : Async<PaperResult> =
        crawlPaperWithAsync CrawlOptions.Default id outDir

    /// Blocking `crawlPaperAsync` — the `id -> outDir -> PaperResult` surface.
    let crawlPaper (id: string) (outDir: string) : PaperResult =
        crawlPaperAsync id outDir |> Async.RunSynchronously

    /// Resolves publication cross-references to the PMCID the full-text endpoints
    /// are keyed on, **without fetching any full text** — the INSDC → EuropePMC
    /// hop, on its own. Prefers a direct `Pmc` ref over a `Pubmed` one that still
    /// needs resolving, resolves the first via EuropePMC (`resolveArticleAsync`),
    /// and logs what it found (`DiscoveredPaperRef`). `None` means there were no
    /// refs, or none that resolved to a PMCID — either way there is no id to
    /// fetch with.
    ///
    /// This is the resolve step as a first-class function, so a caller can compose
    /// it freely: pair it with `crawlPaperFormatWithAsync` for one specific format
    /// (what R1 needs), or with `crawlPaperWithAsync` to take whichever format is
    /// available (what R2 needs). Feed it `Crawler.paperRefs` to go from a project
    /// accession, or `publicationRefs` to go from records you already hold.
    let resolvePmcidWithAsync (options: CrawlOptions) (publications: PublicationRef list) : Async<string option> =
        async {
            let ordered =
                publications
                |> List.sortBy (function
                    | Pmc _ -> 0
                    | Pubmed _ -> 1)

            match ordered with
            | [] -> return None
            | publication :: _ ->
                let! article = resolveArticleAsync options publication

                let refLabel =
                    match publication with
                    | Pubmed pmid -> sprintf "PUBMED:%s" pmid
                    | Pmc pmcid -> sprintf "PMC:%s" pmcid

                let pmcid = article |> Option.bind (fun a -> a.Pmcid)
                options.Log(DiscoveredPaperRef(refLabel, pmcid))
                return pmcid
        }

    /// `resolvePmcidWithAsync` with `CrawlOptions.Default`.
    let resolvePmcidAsync (publications: PublicationRef list) : Async<string option> =
        resolvePmcidWithAsync CrawlOptions.Default publications

    /// Blocking `resolvePmcidAsync`.
    let resolvePmcid (publications: PublicationRef list) : string option =
        resolvePmcidAsync publications |> Async.RunSynchronously

    /// Discovers the paper for a record straight from its own `Link`s and resolves
    /// it to a PMCID — `publicationRefs` followed by `resolvePmcidWithAsync`, for
    /// callers who hold records rather than refs.
    let discoverPmcidWithAsync (options: CrawlOptions) (links: seq<Link>) : Async<string option> =
        resolvePmcidWithAsync options (publicationRefs links)

    /// Discovers a paper for a record straight from its own links
    /// (`discoverPmcidWithAsync`) and fetches its full text in whichever format
    /// is available (`crawlPaperWithAsync`). `PaperResult.NotFound` means no
    /// publication xref on the records, or one that did not resolve to a PMCID,
    /// or a resolved PMCID with no full text available.
    let discoverAndCrawlWithAsync (options: CrawlOptions) (links: seq<Link>) (outDir: string) : Async<PaperResult> =
        async {
            let! pmcid = discoverPmcidWithAsync options links

            match pmcid with
            | Some id -> return! crawlPaperWithAsync options id outDir
            | None -> return NotFound
        }
