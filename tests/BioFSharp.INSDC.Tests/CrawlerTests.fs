namespace BioFSharp.INSDC.Tests

open System
open System.Collections
open System.IO
open System.Reflection
open System.Xml.Linq
open Xunit

open OBO.NET

open BioFSharp.FileFormats.INSDC
open BioFSharp.IO.INSDC

open Arc.Build
open BioFSharp.INSDC.ArcIR

// Scoped here (not at the top of the file) so the crawler namespace does not
// disturb the earlier tests. `BioFSharp.INSDC.Crawler` exposes no `BioProject`
// etc., so it does not clash with the IO reader modules opened above; the SQLite
// store IS referenced fully-qualified for the same reason.
open BioFSharp.INSDC.Crawler

/// Offline fixtures for the crawler tests: a stubbed `Fetch` that maps the ENA
/// URLs the crawler builds to the committed record/report fixtures, so a crawl
/// runs end-to-end with no network access (AGENTS.md forbids network at test time).
module private CrawlerFixtures =

    /// Maps a crawl URL to its committed fixture body (the discovery report, or
    /// the `*_SET` XML for one accession). Order matters: the filereport URL also
    /// contains the project accession, so it is matched first.
    let stubFetch (url: string) : Async<string> =
        async {
            let fixture =
                if url.Contains "filereport" then "crawl-PRJDB5192.filereport.tsv"
                elif url.Contains "DRR072834" then "DRR072834.xml"
                elif url.Contains "DRX066772" then "DRX066772.xml"
                elif url.Contains "SAMD00064197" then "SAMD00064197.xml"
                elif url.Contains "DRP003416" then "DRP003416.xml"
                elif url.Contains "PRJDB5192" then "PRJDB5192.xml"
                else failwithf "unexpected crawl URL: %s" url

            return TestFiles.fixtureText fixture
        }

    /// Crawl options wired for offline, deterministic tests.
    let options: CrawlOptions =
        { CrawlOptions.Default with
            Fetch = stubFetch
            Log = Log.silent
            ThrottleMs = 0 }

type CrawlerTests() =

    [<Fact>]
    member _.``Discovery.parse extracts the connected accessions and relationships`` () =
        let discovered = Discovery.parse (TestFiles.fixtureText "crawl-PRJDB5192.filereport.tsv")
        Assert.Equal<string[]>([| "PRJDB5192" |], List.toArray discovered.BioProjects)
        Assert.Equal<string[]>([| "DRP003416" |], List.toArray discovered.Studies)
        Assert.Equal<string[]>([| "SAMD00064197" |], List.toArray discovered.BioSamples)
        Assert.Equal<string[]>([| "DRX066772" |], List.toArray discovered.Experiments)
        Assert.Equal<string[]>([| "DRR072834" |], List.toArray discovered.Runs)
        // parent relationships used to thread the SQLite foreign keys:
        Assert.Equal("PRJDB5192", Map.find "DRP003416" discovered.StudyToProject)
        Assert.Equal("DRP003416", Map.find "DRX066772" discovered.ExperimentToStudy)
        Assert.Equal("DRX066772", Map.find "DRR072834" discovered.RunToExperiment)
        // the run's FASTQ files (semicolon-separated fastq_ftp/md5/bytes, aligned):
        let row = discovered.Rows |> List.exactlyOne
        Assert.Equal(2, row.FastqFiles.Length)
        Assert.EndsWith("DRR072834_1.fastq.gz", row.FastqFiles.[0].Url)
        Assert.Equal("md5aaa", row.FastqFiles.[0].Md5)
        Assert.Equal("222", row.FastqFiles.[1].Bytes)

    [<Fact>]
    member _.``Endpoints build the expected portal and browser URLs`` () =
        let portal = Endpoints.portalFileReport Endpoints.DefaultPortalBaseUrl "PRJDB5192"
        Assert.Contains("accession=PRJDB5192", portal)
        Assert.Contains("result=read_run", portal)
        Assert.Contains("format=tsv", portal)

        let browser = Endpoints.browserXml Endpoints.DefaultBrowserBaseUrl [ "DRR1"; "DRR2" ]
        Assert.Equal("https://www.ebi.ac.uk/ena/browser/api/xml/DRR1,DRR2", browser)

    [<Fact>]
    member _.``crawl returns the connected records (round trip into types)`` () =
        let result =
            Crawler.crawlWithAsync CrawlerFixtures.options "PRJDB5192"
            |> Async.RunSynchronously

        let expectedProject = BioProject.read (TestFiles.fixture "PRJDB5192.xml") |> Seq.exactlyOne
        Assert.Equal(1, result.BioProjects.Length)
        ObjectGraph.equal expectedProject (Array.exactlyOne result.BioProjects)

        Assert.Equal("DRP003416", (Array.exactlyOne result.Studies).Accession)
        Assert.Equal("SAMD00064197", (Array.exactlyOne result.BioSamples).Accession)
        Assert.Equal("DRX066772", (Array.exactlyOne result.Experiments).Accession)

        let expectedRun = Run.read (TestFiles.fixture "DRR072834.xml") |> Seq.exactlyOne
        ObjectGraph.equal expectedRun (Array.exactlyOne result.Runs)

    [<Fact>]
    member _.``crawlToSqlite persists every entity and the connectivity relation (round trip into sqlite)`` () =
        let dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlite")

        try
            Crawler.crawlToSqliteWithAsync CrawlerFixtures.options "PRJDB5192" dbPath
            |> Async.RunSynchronously

            (
                use connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}")
                connection.Open()

                // Every entity kind is persisted and reconstructable by accession.
                // Full structural fidelity of the store is the store's own concern
                // (it is a normalized subset — e.g. it does not round-trip
                // BioProject.SubmissionProject); the crawler's job of parsing
                // records faithfully is covered by the "round trip into types" test
                // above, so here we check identity + a representative stored field.
                let expectedProject = BioProject.read (TestFiles.fixture "PRJDB5192.xml") |> Seq.exactlyOne
                match BioFSharp.INSDC.SQLite.BioProject.tryGet connection "PRJDB5192" with
                | Some stored ->
                    Assert.Equal("PRJDB5192", stored.Accession)
                    Assert.Equal(expectedProject.Title, stored.Title)
                | None -> Assert.True(false, "BioProject PRJDB5192 was not persisted")

                let expectedRun = Run.read (TestFiles.fixture "DRR072834.xml") |> Seq.exactlyOne
                match BioFSharp.INSDC.SQLite.Run.tryGet connection "DRR072834" with
                | Some stored ->
                    Assert.Equal("DRR072834", stored.Accession)
                    Assert.Equal(expectedRun.Title, stored.Title)
                | None -> Assert.True(false, "Run DRR072834 was not persisted")

                Assert.True((BioFSharp.INSDC.SQLite.Study.tryGet connection "DRP003416").IsSome, "Study not persisted")
                Assert.True((BioFSharp.INSDC.SQLite.BioSample.tryGet connection "SAMD00064197").IsSome, "Sample not persisted")
                Assert.True((BioFSharp.INSDC.SQLite.Experiment.tryGet connection "DRX066772").IsSome, "Experiment not persisted")

                // The connectivity relation resolves run -> everything in one row.
                match BioFSharp.INSDC.SQLite.AccessionRelations.tryGet connection "DRR072834" with
                | Some relation ->
                    Assert.Equal("DRX066772", relation.ExperimentAccession)
                    Assert.Equal("SAMD00064197", relation.SampleAccession)
                    Assert.Equal("DRP003416", relation.StudyAccession)
                    Assert.Equal("PRJDB5192", relation.ProjectAccession)
                    Assert.Equal("PRJDB5192", relation.RootAccession)
                | None -> Assert.True(false, "accession_relations row for DRR072834 was not persisted")
            )
        finally
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools() |> ignore

            if File.Exists dbPath then
                File.Delete dbPath

    [<Fact>]
    member _.``crawlToSqlite is idempotent across re-runs (resume)`` () =
        let dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlite")

        try
            Crawler.crawlToSqliteWithAsync CrawlerFixtures.options "PRJDB5192" dbPath |> Async.RunSynchronously
            // A second run over the same DB must not throw on primary-key collisions.
            Crawler.crawlToSqliteWithAsync CrawlerFixtures.options "PRJDB5192" dbPath |> Async.RunSynchronously

            (
                use connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}")
                connection.Open()

                Assert.Equal<string[]>(
                    [| "PRJDB5192" |],
                    BioFSharp.INSDC.SQLite.BioProject.listAccessions connection |> Seq.toArray)

                Assert.Equal<string[]>(
                    [| "DRR072834" |],
                    BioFSharp.INSDC.SQLite.Run.listAccessions connection |> Seq.toArray)
            )
        finally
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools() |> ignore

            if File.Exists dbPath then
                File.Delete dbPath

    [<Fact>]
    member _.``Discovery.withRoot seeds a childless root into the bucket its prefix implies`` () =
        // A childless project/study yields a header-only report -> the empty set.
        let empty =
            Discovery.parse
                "run_accession\texperiment_accession\tsample_accession\tstudy_accession\tsecondary_study_accession\n"
        Assert.Empty empty.BioProjects
        Assert.Empty empty.Studies

        // The root is seeded so its own record is still fetched: PRJ... -> project,
        // SRP/ERP/DRP... -> study.
        Assert.Equal<string[]>([| "PRJNA999" |], Discovery.withRoot "PRJNA999" empty |> fun s -> List.toArray s.BioProjects)
        Assert.Empty((Discovery.withRoot "PRJNA999" empty).Studies)
        Assert.Equal<string[]>([| "SRP123456" |], Discovery.withRoot "SRP123456" empty |> fun s -> List.toArray s.Studies)

        // An already-present root is not duplicated; an unrecognized prefix
        // (e.g. a run accession) leaves every bucket untouched.
        let parsed = Discovery.parse (TestFiles.fixtureText "crawl-PRJDB5192.filereport.tsv")
        Assert.Equal<string[]>([| "PRJDB5192" |], Discovery.withRoot "PRJDB5192" parsed |> fun s -> List.toArray s.BioProjects)
        Assert.Equal<string[]>(List.toArray parsed.BioProjects, Discovery.withRoot "DRR999" parsed |> fun s -> List.toArray s.BioProjects)

    [<Fact>]
    member _.``crawlToSqlite persists a childless project (no runs) via root seeding`` () =
        // ENA returns a header-only filereport for a project with no runs, so
        // discovery finds nothing to relate. The root must still be stored.
        let headerOnly =
            "run_accession\texperiment_accession\tsample_accession\tstudy_accession\t\
             secondary_study_accession\tfastq_ftp\tfastq_md5\tfastq_bytes\n"

        let stub (url: string) : Async<string> =
            async {
                return
                    if url.Contains "filereport" then headerOnly
                    elif url.Contains "PRJDB5192" then TestFiles.fixtureText "PRJDB5192.xml"
                    else failwithf "unexpected crawl URL: %s" url
            }

        let options = { CrawlOptions.Default with Fetch = stub; Log = Log.silent; ThrottleMs = 0 }
        let dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlite")

        try
            Crawler.crawlToSqliteWithAsync options "PRJDB5192" dbPath |> Async.RunSynchronously

            (
                use connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}")
                connection.Open()

                Assert.True(
                    (BioFSharp.INSDC.SQLite.BioProject.tryGet connection "PRJDB5192").IsSome,
                    "childless BioProject was not persisted")

                // No runs, so the connectivity table is legitimately empty.
                Assert.Empty(BioFSharp.INSDC.SQLite.AccessionRelations.listAccessions connection |> Seq.toList)
            )
        finally
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools() |> ignore

            if File.Exists dbPath then
                File.Delete dbPath

    [<Fact>]
    member _.``crawl emits a Started event carrying the root accession as its first event`` () =
        let events = System.Collections.Generic.List<CrawlEvent>()
        let options = { CrawlerFixtures.options with Log = events.Add }

        Crawler.crawlWithAsync options "PRJDB5192" |> Async.RunSynchronously |> ignore

        match Seq.tryHead events with
        | Some (Started accession) -> Assert.Equal("PRJDB5192", accession)
        | other -> Assert.Fail(sprintf "expected a Started event first, got %A" other)

    [<Fact>]
    member _.``Sql.withTransaction is reentrant — a nested call joins the outer transaction`` () =
        use connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:")
        connection.Open()
        let exec sql = BioFSharp.INSDC.SQLite.Internal.Sql.execNonQuery connection sql [] |> ignore
        let count () =
            BioFSharp.INSDC.SQLite.Internal.Sql.queryAll connection "SELECT COUNT(*) FROM t;" [] (fun r -> r.GetInt32 0)
            |> List.head

        exec "CREATE TABLE t (v INTEGER);"

        // A nested withTransaction must not throw (SQLite has no nested
        // transactions) — it joins the outer one, so both inserts commit together.
        BioFSharp.INSDC.SQLite.Internal.Sql.withTransaction connection (fun _ ->
            exec "INSERT INTO t VALUES (1);"
            BioFSharp.INSDC.SQLite.Internal.Sql.withTransaction connection (fun _ ->
                exec "INSERT INTO t VALUES (2);"))
        Assert.Equal(2, count ())

        // When the outer rolls back, the joined-inner write is discarded too —
        // proof the inner did not commit independently.
        try
            BioFSharp.INSDC.SQLite.Internal.Sql.withTransaction connection (fun _ ->
                exec "INSERT INTO t VALUES (3);"
                BioFSharp.INSDC.SQLite.Internal.Sql.withTransaction connection (fun _ ->
                    exec "INSERT INTO t VALUES (4);")
                failwith "boom")
        with _ -> ()

        Assert.Equal(2, count ())

    [<Fact>]
    member _.``LIVE crawl of a small public project (opt-in via INSDC_LIVE_TESTS=1)`` () =
        // Off by default (AGENTS.md forbids network at test time). Set
        // INSDC_LIVE_TESTS=1 to actually hit ENA and exercise the FsHttp path.
        if System.Environment.GetEnvironmentVariable "INSDC_LIVE_TESTS" = "1" then
            let result = Crawler.crawl "PRJDB5192"
            Assert.NotEmpty result.BioSamples
            Assert.NotEmpty result.Experiments
            Assert.NotEmpty result.Runs


// ---- R2 raw-artifact crawler tests (crawlToXml, Paper, Dee2, crawlAll) ----

/// Shared helpers for the R2 crawler test modules. Scoped here (not at the top
/// of the file) so the crawler namespace does not disturb the earlier tests.
module private R2TestHelpers =

    /// Stubbed `FetchBytes` returning the committed PDF fixture (for the paper
    /// PDF fallback path). Any URL gets the same bytes — the test only needs
    /// `bytes.Length > 0` to trigger the PDF branch.
    let stubFetchBytes (_url: string) : Async<byte[]> =
        async { return File.ReadAllBytes(TestFiles.fixture "paper-PRJDB5192.pdf") }

    /// Builds a `Link` carrying a single `XREF_LINK` with the given DB/ID —
    /// for the paper-discovery tests, which read publication xrefs off records.
    let xref (db: string) (id: string) : Link =
        let link = Link()
        link.XrefLink <- XRef(Db = db, Id = id)
        link

type CrawlerXmlTests() =

    [<Fact>]
    member _.``crawlToXml writes the project-shaped XML tree`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r2xml-{Guid.NewGuid():N}")

        try
            Crawler.crawlToXmlWithAsync CrawlerFixtures.options "PRJDB5192" outDir
            |> Async.RunSynchronously

            // BioProject at root.
            let rootXmls = Directory.GetFiles(outDir, "*.xml")
            Assert.Contains(rootXmls, fun f -> Path.GetFileName f = "PRJDB5192.xml")
            // Study at root.
            Assert.Contains(rootXmls, fun f -> Path.GetFileName f = "DRP003416.xml")

            // samples/ subfolder.
            let samples = Path.Combine(outDir, "samples")
            Assert.True(Directory.Exists samples, "samples/ folder missing")
            Assert.Contains(Directory.GetFiles(samples, "*.xml"), fun f -> Path.GetFileName f = "SAMD00064197.xml")

            // experiments/ subfolder.
            let experiments = Path.Combine(outDir, "experiments")
            Assert.True(Directory.Exists experiments, "experiments/ folder missing")
            Assert.Contains(
                Directory.GetFiles(experiments, "*.xml"),
                fun f -> Path.GetFileName f = "DRX066772.xml")

            // runs/ subfolder.
            let runs = Path.Combine(outDir, "runs")
            Assert.True(Directory.Exists runs, "runs/ folder missing")
            Assert.Contains(Directory.GetFiles(runs, "*.xml"), fun f -> Path.GetFileName f = "DRR072834.xml")
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``crawlToXml is idempotent — re-run writes no new files`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r2xml-resume-{Guid.NewGuid():N}")

        try
            Crawler.crawlToXmlWithAsync CrawlerFixtures.options "PRJDB5192" outDir
            |> Async.RunSynchronously

            let countBefore = Directory.GetFiles(outDir, "*.xml", SearchOption.AllDirectories).Length

            // Second run: all files already exist → skipped, no new files.
            Crawler.crawlToXmlWithAsync CrawlerFixtures.options "PRJDB5192" outDir
            |> Async.RunSynchronously

            let countAfter = Directory.GetFiles(outDir, "*.xml", SearchOption.AllDirectories).Length
            Assert.Equal(countBefore, countAfter)
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

type PaperCrawlerTests() =

    [<Fact>]
    member _.``EuropePMC endpoints build the expected full-text URLs`` () =
        let xml = Endpoints.europePmcFullTextXml Endpoints.DefaultEuropePmcBaseUrl "PMC123456"
        Assert.Equal("https://www.ebi.ac.uk/europepmc/webservices/rest/PMC123456/fullTextXML", xml)

        // The PDF does NOT come from EuropePMC: its `fullTextPDF` path 404s for every
        // article. PDFs come from the PMC Open Access dataset on AWS, keyed by
        // versioned PMCID.
        let pdf = Endpoints.pmcOaPdf Endpoints.DefaultPmcOaBaseUrl "PMC7430643" 1
        Assert.Equal("https://pmc-oa-opendata.s3.amazonaws.com/PMC7430643.1/PMC7430643.1.pdf", pdf)

    [<Fact>]
    member _.``crawlPaper writes JATS XML when full text is available`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r2paper-jats-{Guid.NewGuid():N}")

        let stub (url: string) : Async<string> =
            async { return TestFiles.fixtureText "paper-PRJDB5192.jats.xml" }

        let opts = { CrawlOptions.Default with Fetch = stub; Log = Log.silent; ThrottleMs = 0 }

        try
            let result = Paper.crawlPaperWithAsync opts "PMC123456" outDir |> Async.RunSynchronously

            match result with
            | PaperResult.JatsXml path ->
                Assert.EndsWith("PMC123456.jats.xml", path)
                Assert.True(File.Exists path)
            | other -> Assert.Fail(sprintf "expected JatsXml, got %A" other)
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``crawlPaper falls back to PDF when JATS XML is not available`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r2paper-pdf-{Guid.NewGuid():N}")

        // XML fetch always fails (no open-access full text).
        let stubXml (_url: string) : Async<string> =
            async { return failwith "404 not found" }

        let opts =
            { CrawlOptions.Default with
                Fetch = stubXml
                FetchBytes = R2TestHelpers.stubFetchBytes
                Log = Log.silent
                ThrottleMs = 0 }

        try
            let result = Paper.crawlPaperWithAsync opts "PMC999999" outDir |> Async.RunSynchronously

            match result with
            | PaperResult.Pdf path ->
                Assert.EndsWith("PMC999999.pdf", path)
                Assert.True(File.Exists path)
            | other -> Assert.Fail(sprintf "expected Pdf, got %A" other)
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``crawlPaper returns NotFound when both XML and PDF fail`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r2paper-none-{Guid.NewGuid():N}")

        let stubFail (_url: string) : Async<string> = async { return failwith "404" }
        let stubFailBytes (_url: string) : Async<byte[]> = async { return failwith "404" }

        let opts =
            { CrawlOptions.Default with
                Fetch = stubFail
                FetchBytes = stubFailBytes
                Log = Log.silent
                ThrottleMs = 0 }

        try
            let result = Paper.crawlPaperWithAsync opts "PMC000000" outDir |> Async.RunSynchronously
            Assert.Equal(PaperResult.NotFound, result)
            // No paper folder should have been created.
            Assert.False(Directory.Exists(Path.Combine(outDir, "paper")))
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``crawlPaper sanitizes DOI slashes in the filename`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r2paper-doi-{Guid.NewGuid():N}")

        let stub (_url: string) : Async<string> =
            async { return TestFiles.fixtureText "paper-PRJDB5192.jats.xml" }

        let opts = { CrawlOptions.Default with Fetch = stub; Log = Log.silent; ThrottleMs = 0 }

        try
            let result = Paper.crawlPaperWithAsync opts "10.1000/testgenomics.2017.001" outDir |> Async.RunSynchronously

            match result with
            | PaperResult.JatsXml path -> Assert.EndsWith("10.1000_testgenomics.2017.001.jats.xml", path)
            | other -> Assert.Fail(sprintf "expected JatsXml, got %A" other)
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``crawlPaperFormat Jats writes the JATS XML and never probes the PDF`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r1paper-jats-{Guid.NewGuid():N}")

        let stub (_url: string) : Async<string> =
            async { return TestFiles.fixtureText "paper-PRJDB5192.jats.xml" }

        // A forced-JATS fetch must never touch the binary (PDF) seam.
        let stubBytes (url: string) : Async<byte[]> =
            async { return failwithf "must not fetch the PDF: %s" url }

        let opts =
            { CrawlOptions.Default with
                Fetch = stub
                FetchBytes = stubBytes
                Log = Log.silent
                ThrottleMs = 0 }

        try
            let result =
                Paper.crawlPaperFormatWithAsync opts PaperFormat.Jats "PMC123456" outDir
                |> Async.RunSynchronously

            match result with
            | PaperResult.JatsXml path ->
                Assert.EndsWith("PMC123456.jats.xml", path)
                Assert.True(File.Exists path)
            | other -> Assert.Fail(sprintf "expected JatsXml, got %A" other)
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``crawlPaperFormat Jats returns NotFound rather than falling back to the PDF`` () =
        // This is what keeps R1A honest: a JATS miss must NOT silently become an
        // R1B. The PDF stub below would succeed if it were ever reached.
        let outDir = Path.Combine(Path.GetTempPath(), $"r1paper-jats-miss-{Guid.NewGuid():N}")

        let stubXml (_url: string) : Async<string> = async { return failwith "404 not found" }

        let mutable pdfFetched = false

        let stubBytes (_url: string) : Async<byte[]> =
            async {
                pdfFetched <- true
                return File.ReadAllBytes(TestFiles.fixture "paper-PRJDB5192.pdf")
            }

        let opts =
            { CrawlOptions.Default with
                Fetch = stubXml
                FetchBytes = stubBytes
                Log = Log.silent
                ThrottleMs = 0 }

        try
            let result =
                Paper.crawlPaperFormatWithAsync opts PaperFormat.Jats "PMC999999" outDir
                |> Async.RunSynchronously

            Assert.Equal(PaperResult.NotFound, result)
            Assert.False(pdfFetched, "a forced-JATS fetch must not fall back to the PDF")
            Assert.False(Directory.Exists(Path.Combine(outDir, "paper")))
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``crawlPaperFormat Pdf fetches from the PMC OA bucket, never EuropePMC`` () =
        // A regression test for a real production bug: the PDF used to be fetched from
        // EuropePMC's `fullTextPDF`, which 404s for EVERY article — so `PaperResult.Pdf`
        // was unreachable in production while the offline tests passed happily against a
        // stubbed byte array. PDFs come from the PMC Open Access dataset on AWS.
        let outDir = Path.Combine(Path.GetTempPath(), $"paper-oa-{Guid.NewGuid():N}")
        let requested = System.Collections.Concurrent.ConcurrentBag<string>()

        let stubBytes (url: string) : Async<byte[]> =
            async {
                requested.Add url

                if url.Contains "fullTextPDF" then
                    return failwith "EuropePMC has no working PDF endpoint — must not be called"

                return File.ReadAllBytes(TestFiles.fixture "paper-PRJDB5192.pdf")
            }

        let opts =
            { CrawlOptions.Default with
                Fetch = (fun url -> async { return failwithf "must not fetch text: %s" url })
                FetchBytes = stubBytes
                Log = Log.silent
                ThrottleMs = 0 }

        try
            let result =
                Paper.crawlPaperFormatWithAsync opts PaperFormat.Pdf "PMC7430643" outDir
                |> Async.RunSynchronously

            match result with
            | PaperResult.Pdf path -> Assert.True(File.Exists path)
            | other -> Assert.Fail(sprintf "expected Pdf, got %A" other)

            let urls = requested |> Seq.toList
            Assert.Contains(urls, fun (u: string) -> u.Contains "pmc-oa-opendata")
            Assert.DoesNotContain(urls, fun (u: string) -> u.Contains "fullTextPDF")

            // Version 1 satisfies it, so the ladder costs exactly one request.
            Assert.Equal(1, List.length urls)
            Assert.Contains("PMC7430643.1/PMC7430643.1.pdf", List.head urls)
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``crawlPaperFormat Pdf walks the version ladder when v1 is absent`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"paper-oa-v2-{Guid.NewGuid():N}")

        // Only version 2 exists — v1 404s, as it does for a revised article.
        let stubBytes (url: string) : Async<byte[]> =
            async {
                if url.Contains ".2/" then
                    return File.ReadAllBytes(TestFiles.fixture "paper-PRJDB5192.pdf")
                else
                    return failwith "404"
            }

        let opts =
            { CrawlOptions.Default with
                FetchBytes = stubBytes
                Log = Log.silent
                ThrottleMs = 0
                Retries = 0 }

        try
            let result =
                Paper.crawlPaperFormatWithAsync opts PaperFormat.Pdf "PMC7430643" outDir
                |> Async.RunSynchronously

            match result with
            | PaperResult.Pdf path -> Assert.True(File.Exists path)
            | other -> Assert.Fail(sprintf "expected Pdf from version 2, got %A" other)
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``LIVE the PMC OA bucket really serves a PDF (opt-in via INSDC_LIVE_TESTS=1)`` () =
        // The ONLY kind of test that would have caught the dead-endpoint bug: every
        // offline test stubs the bytes, so a wholly fictitious URL passes them all.
        if System.Environment.GetEnvironmentVariable "INSDC_LIVE_TESTS" = "1" then
            let outDir = Path.Combine(Path.GetTempPath(), $"paper-live-{Guid.NewGuid():N}")

            try
                match Paper.crawlPaperFormat PaperFormat.Pdf "PMC7430643" outDir with
                | PaperResult.Pdf path ->
                    let bytes = File.ReadAllBytes path
                    Assert.True(bytes.Length > 10_000, "a real article PDF should not be tiny")
                    // A real PDF starts with the %PDF- magic; an HTML error page does not.
                    Assert.Equal<byte[]>("%PDF-"B, bytes.[.. 4])
                | other -> Assert.Fail(sprintf "expected a real Pdf, got %A" other)
            finally
                if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``crawlPaperFormat Pdf fetches the PDF directly with no JATS probe`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r1paper-pdf-{Guid.NewGuid():N}")

        // A forced-PDF fetch must never touch the text seam — no wasted JATS probe.
        let stub (url: string) : Async<string> =
            async { return failwithf "must not fetch the JATS XML: %s" url }

        let opts =
            { CrawlOptions.Default with
                Fetch = stub
                FetchBytes = R2TestHelpers.stubFetchBytes
                Log = Log.silent
                ThrottleMs = 0 }

        try
            let result =
                Paper.crawlPaperFormatWithAsync opts PaperFormat.Pdf "PMC123456" outDir
                |> Async.RunSynchronously

            match result with
            | PaperResult.Pdf path ->
                Assert.EndsWith("PMC123456.pdf", path)
                Assert.True(File.Exists path)
            | other -> Assert.Fail(sprintf "expected Pdf, got %A" other)
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

type PaperDiscoveryTests() =

    [<Fact>]
    member _.``europePmcSearch builds the escaped JSON search URL`` () =
        let url = Endpoints.europePmcSearch Endpoints.DefaultEuropePmcBaseUrl "EXT_ID:18808718 AND SRC:MED" 1

        Assert.Equal(
            "https://www.ebi.ac.uk/europepmc/webservices/rest/search?query=EXT_ID%3A18808718%20AND%20SRC%3AMED&format=json&resultType=lite&pageSize=1",
            url)

    [<Fact>]
    member _.``publicationRefs keeps PUBMED/PMC xrefs and ignores housekeeping links`` () =
        let links =
            [ R2TestHelpers.xref "ENA-FASTQ-FILES" "http://example/fastq"
              R2TestHelpers.xref "PUBMED" "18808718"
              R2TestHelpers.xref "ENA-SUBMISSION" "DRA005154"
              R2TestHelpers.xref "PMC" "2568001" ]

        // PUBMED/PMC kept (PMC id normalized to the PMCXXXXX form); the rest dropped.
        Assert.Equal<PublicationRef list>([ Pubmed "18808718"; Pmc "PMC2568001" ], Paper.publicationRefs links)

    [<Fact>]
    member _.``publicationRefs on null or empty links yields empty`` () =
        Assert.Empty(Paper.publicationRefs null)
        Assert.Empty(Paper.publicationRefs [])

    [<Fact>]
    member _.``parseSearchResults maps the EuropePMC search fixture into an Article`` () =
        let article =
            Paper.parseSearchResults (TestFiles.fixtureText "europepmc-search-18808718.json")
            |> List.exactlyOne

        Assert.Equal(Some "18808718", article.Pmid)
        Assert.Equal(Some "PMC2568001", article.Pmcid)
        Assert.Equal(Some "10.1186/1471-2164-9-434", article.Doi)
        Assert.True(article.IsOpenAccess)

    [<Fact>]
    member _.``parseSearchResults on an empty result list yields empty`` () =
        Assert.Empty(Paper.parseSearchResults """{"hitCount":0,"resultList":{"result":[]}}""")

    [<Fact>]
    member _.``resolveArticleAsync maps a PubMed ref to its PMCID via search`` () =
        let stub (url: string) : Async<string> =
            async {
                Assert.Contains("search?query=", url)
                Assert.Contains("EXT_ID%3A18808718", url)
                return TestFiles.fixtureText "europepmc-search-18808718.json"
            }

        let opts = { CrawlOptions.Default with Fetch = stub; Log = Log.silent; ThrottleMs = 0 }

        match Paper.resolveArticleAsync opts (Pubmed "18808718") |> Async.RunSynchronously with
        | Some article -> Assert.Equal(Some "PMC2568001", article.Pmcid)
        | None -> Assert.Fail("expected an article")

    [<Fact>]
    member _.``resolveArticleAsync swallows a search failure to None`` () =
        let stubFail (_url: string) : Async<string> = async { return failwith "500" }
        let opts = { CrawlOptions.Default with Fetch = stubFail; Log = Log.silent; ThrottleMs = 0 }

        Assert.Equal(None, Paper.resolveArticleAsync opts (Pubmed "18808718") |> Async.RunSynchronously)

    [<Fact>]
    member _.``discoverAndCrawl resolves a record's PUBMED xref and writes JATS`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r2discover-{Guid.NewGuid():N}")

        // The discovery search resolves PMID 18808718 -> PMC2568001; the
        // full-text fetch must then be keyed on that PMCID.
        let stub (url: string) : Async<string> =
            async {
                if url.Contains "search?query=" then
                    return TestFiles.fixtureText "europepmc-search-18808718.json"
                elif url.Contains "fullTextXML" then
                    Assert.Contains("PMC2568001", url)
                    return TestFiles.fixtureText "paper-PRJDB5192.jats.xml"
                else
                    return failwithf "unexpected URL: %s" url
            }

        let opts = { CrawlOptions.Default with Fetch = stub; Log = Log.silent; ThrottleMs = 0 }

        try
            let result =
                Paper.discoverAndCrawlWithAsync opts [ R2TestHelpers.xref "PUBMED" "18808718" ] outDir
                |> Async.RunSynchronously

            match result with
            | PaperResult.JatsXml path ->
                Assert.EndsWith("PMC2568001.jats.xml", path)
                Assert.True(File.Exists path)
            | other -> Assert.Fail(sprintf "expected JatsXml, got %A" other)
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``discoverAndCrawl with no publication xref returns NotFound and fetches nothing`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r2discover-none-{Guid.NewGuid():N}")
        let stub (url: string) : Async<string> = async { return failwithf "should not fetch: %s" url }
        let opts = { CrawlOptions.Default with Fetch = stub; Log = Log.silent; ThrottleMs = 0 }

        try
            let result =
                Paper.discoverAndCrawlWithAsync opts [ R2TestHelpers.xref "ENA-FASTQ-FILES" "http://x" ] outDir
                |> Async.RunSynchronously

            Assert.Equal(PaperResult.NotFound, result)
            Assert.False(Directory.Exists(Path.Combine(outDir, "paper")))
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``discoverPmcid resolves a PUBMED xref to its PMCID and fetches no full text`` () =
        let stub (url: string) : Async<string> =
            async {
                if url.Contains "search?query=" then
                    return TestFiles.fixtureText "europepmc-search-18808718.json"
                else
                    return failwithf "discovery must not fetch full text: %s" url
            }

        let opts = { CrawlOptions.Default with Fetch = stub; Log = Log.silent; ThrottleMs = 0 }

        let pmcid =
            Paper.discoverPmcidWithAsync opts [ R2TestHelpers.xref "PUBMED" "18808718" ]
            |> Async.RunSynchronously

        Assert.Equal<string option>(Some "PMC2568001", pmcid)

    [<Fact>]
    member _.``discoverPmcid returns None for housekeeping-only links`` () =
        let stub (url: string) : Async<string> = async { return failwithf "should not fetch: %s" url }
        let opts = { CrawlOptions.Default with Fetch = stub; Log = Log.silent; ThrottleMs = 0 }

        let pmcid =
            Paper.discoverPmcidWithAsync opts [ R2TestHelpers.xref "ENA-FASTQ-FILES" "http://x" ]
            |> Async.RunSynchronously

        Assert.Equal<string option>(None, pmcid)

type Dee2CrawlerTests() =

    [<Fact>]
    member _.``dee2Search builds the expected search2.sh URL`` () =
        let url = Endpoints.dee2Search Endpoints.DefaultDee2SearchBaseUrl "athaliana" "DRP003416"
        Assert.Equal("http://dee2.io/cgi-bin/search2.sh?org=athaliana&accessionsearch=DRP003416", url)

    [<Fact>]
    member _.``parseSearchResult extracts the bundle zip URL from a real search result page`` () =
        // The fixture is DEE2's REAL `search2.sh` response, not a hand-written one. Two
        // things the old made-up fixture got wrong, and so never exercised: the link is
        // served over HTTPS, and the bundle carries an `_NA` suffix
        // (`DRP003416_NA.zip`), which is why the crawler renames it to
        // `counts/<accession>.zip` rather than trusting the remote filename.
        let html = TestFiles.fixtureText "dee2-search-DRP003416.html"

        match Dee2.parseSearchResult html with
        | Some url -> Assert.Equal("https://dee2.io/huge/athaliana/DRP003416_NA.zip", url)
        | None -> Assert.Fail("expected Some url, got None")

    [<Fact>]
    member _.``parseSearchResult returns None on a no-results page`` () =
        Assert.Equal(None, Dee2.parseSearchResult "<html><body><p>No results found</p></body></html>")

    [<Fact>]
    member _.``resolveBundleUrlWithAsync returns the bundle URL via stubbed fetch`` () =
        let stub (_url: string) : Async<string> =
            async { return TestFiles.fixtureText "dee2-search-DRP003416.html" }

        let opts = { CrawlOptions.Default with Fetch = stub; Log = Log.silent; ThrottleMs = 0 }

        let url = Dee2.resolveBundleUrlWithAsync opts "athaliana" "DRP003416" |> Async.RunSynchronously

        match url with
        | Some u -> Assert.EndsWith("DRP003416_NA.zip", u)
        | None -> Assert.Fail("expected Some url, got None")

    [<Fact>]
    member _.``crawlDee2 writes the bundle zip when the accession resolves`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r2dee2-found-{Guid.NewGuid():N}")

        let stubSearch (_url: string) : Async<string> =
            async { return TestFiles.fixtureText "dee2-search-DRP003416.html" }

        let zipBytes = File.ReadAllBytes(TestFiles.fixture "dee2-DRP003416.zip")

        let stubBytes (_url: string) : Async<byte[]> =
            async { return zipBytes }

        let opts =
            { CrawlOptions.Default with
                Fetch = stubSearch
                FetchBytes = stubBytes
                Log = Log.silent
                ThrottleMs = 0 }

        try
            let result = Dee2.crawlDee2WithAsync opts "athaliana" "DRP003416" outDir |> Async.RunSynchronously

            match result with
            | Some path ->
                Assert.EndsWith("DRP003416.zip", path)
                Assert.True(File.Exists path)
            | None -> Assert.Fail("expected Some path, got None")
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``crawlDee2 returns None when the accession has no DEE2 bundle`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r2dee2-missing-{Guid.NewGuid():N}")

        let stub (_url: string) : Async<string> =
            async { return "<html><body><p>No results found</p></body></html>" }

        let opts = { CrawlOptions.Default with Fetch = stub; Log = Log.silent; ThrottleMs = 0 }

        try
            let result = Dee2.crawlDee2WithAsync opts "athaliana" "SRP999999" outDir |> Async.RunSynchronously
            Assert.Equal(None, result)
            // No counts/ folder should have been created.
            Assert.False(Directory.Exists(Path.Combine(outDir, "counts")))
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

type CrawlAllTests() =

    /// Crawl options that stub INSDC (filereport + per-accession XML), the
    /// paper JATS path, and the DEE2 listing + bundle fetch. `Fetch` handles
    /// text (filereport, INSDC XML, DEE2 listing); `FetchBytes` handles the
    /// DEE2 zip binary.
    let stubAllOptions (paperJats: bool) : CrawlOptions =
        let stubText (url: string) : Async<string> =
            async {
                if url.Contains "filereport" then
                    return TestFiles.fixtureText "crawl-PRJDB5192.filereport.tsv"
                // DEE2 search URL carries the study accession too (…&accessionsearch=DRP003416),
                // so match it before the accession-keyed INSDC XML branches below.
                elif url.Contains "search2.sh" then
                    return TestFiles.fixtureText "dee2-search-DRP003416.html"
                elif url.Contains "DRR072834" then return TestFiles.fixtureText "DRR072834.xml"
                elif url.Contains "DRX066772" then return TestFiles.fixtureText "DRX066772.xml"
                elif url.Contains "SAMD00064197" then return TestFiles.fixtureText "SAMD00064197.xml"
                elif url.Contains "DRP003416" then return TestFiles.fixtureText "DRP003416.xml"
                elif url.Contains "PRJDB5192" then return TestFiles.fixtureText "PRJDB5192.xml"
                elif url.Contains "fullTextXML" then
                    if paperJats then
                        return TestFiles.fixtureText "paper-PRJDB5192.jats.xml"
                    else
                        return failwith "no open access"
                else return failwithf "unexpected URL: %s" url
            }

        let zipBytes = File.ReadAllBytes(TestFiles.fixture "dee2-DRP003416.zip")

        let stubBytes (url: string) : Async<byte[]> =
            async {
                if url.Contains "huge" then return zipBytes
                // PDFs come from the PMC Open Access bucket, not EuropePMC.
                elif url.Contains "pmc-oa-opendata" then
                    return File.ReadAllBytes(TestFiles.fixture "paper-PRJDB5192.pdf")
                else return failwithf "unexpected binary URL: %s" url
            }

        { CrawlOptions.Default with
            Fetch = stubText
            FetchBytes = stubBytes
            Log = Log.silent
            ThrottleMs = 0 }

    [<Fact>]
    member _.``crawlAll writes INSDC XML + paper + DEE2 under one outDir`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r2all-{Guid.NewGuid():N}")

        try
            let opts = stubAllOptions true

            let summary =
                Crawler.crawlAllWithAsync opts "PRJDB5192" outDir (Crawler.PaperFrom "PMC123456") (Crawler.Dee2Discover "athaliana")
                |> Async.RunSynchronously

            Assert.Equal(outDir, summary.ProjectDir)
            Assert.True(summary.InsdcCounts.["root"] >= 2, "expected BioProject + Study at root")
            Assert.Equal(1, summary.InsdcCounts.["samples"])
            Assert.Equal(1, summary.InsdcCounts.["experiments"])
            Assert.Equal(1, summary.InsdcCounts.["runs"])

            match summary.Paper with
            | PaperResult.JatsXml path -> Assert.True(File.Exists path)
            | other -> Assert.Fail(sprintf "expected JatsXml paper, got %A" other)

            match summary.Dee2Path with
            | Some path -> Assert.True(File.Exists path)
            | None -> Assert.Fail("expected DEE2 zip, got None")
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``crawlAll auto-discovers paper when paperId is None (no xref -> NotFound)`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r2all-nopaper-{Guid.NewGuid():N}")

        try
            let opts = stubAllOptions true

            // paperId = None triggers discovery from the records' publication
            // xrefs. The PRJDB5192/DRP003416 fixtures carry only ENA housekeeping
            // links (no PUBMED/PMC), so discovery finds nothing and fetches no
            // paper — no EuropePMC call, no paper/ folder.
            let summary =
                Crawler.crawlAllWithAsync opts "PRJDB5192" outDir Crawler.PaperDiscover (Crawler.Dee2Discover "athaliana")
                |> Async.RunSynchronously

            Assert.Equal(PaperResult.NotFound, summary.Paper)
            Assert.False(Directory.Exists(Path.Combine(outDir, "paper")))
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``crawlAll skips DEE2 when dee2Species is None`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r2all-nodee2-{Guid.NewGuid():N}")

        try
            let opts = stubAllOptions true

            let summary =
                Crawler.crawlAllWithAsync opts "PRJDB5192" outDir (Crawler.PaperFrom "PMC123456") Crawler.Dee2Skip
                |> Async.RunSynchronously

            Assert.Equal(None, summary.Dee2Path)
            Assert.False(Directory.Exists(Path.Combine(outDir, "counts")))
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``crawlAll falls back to PDF when JATS is unavailable`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r2all-pdf-{Guid.NewGuid():N}")

        try
            let opts = stubAllOptions false

            let summary =
                Crawler.crawlAllWithAsync opts "PRJDB5192" outDir (Crawler.PaperFrom "PMC999999") (Crawler.Dee2Discover "athaliana")
                |> Async.RunSynchronously

            match summary.Paper with
            | PaperResult.Pdf path ->
                Assert.True(File.Exists path)
                Assert.EndsWith(".pdf", path)
            | other -> Assert.Fail(sprintf "expected Pdf, got %A" other)
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``crawlAll returns NotFound when both paper formats fail`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r2all-nopaperatall-{Guid.NewGuid():N}")

        let stubText (url: string) : Async<string> =
            async {
                if url.Contains "filereport" then
                    return TestFiles.fixtureText "crawl-PRJDB5192.filereport.tsv"
                // Match the DEE2 search URL before the accession-keyed INSDC branches
                // (it also carries …&accessionsearch=DRP003416).
                elif url.Contains "search2.sh" then
                    return TestFiles.fixtureText "dee2-search-DRP003416.html"
                elif url.Contains "DRR072834" then return TestFiles.fixtureText "DRR072834.xml"
                elif url.Contains "DRX066772" then return TestFiles.fixtureText "DRX066772.xml"
                elif url.Contains "SAMD00064197" then return TestFiles.fixtureText "SAMD00064197.xml"
                elif url.Contains "DRP003416" then return TestFiles.fixtureText "DRP003416.xml"
                elif url.Contains "PRJDB5192" then return TestFiles.fixtureText "PRJDB5192.xml"
                else return failwith "no paper"
            }

        let zipBytes = File.ReadAllBytes(TestFiles.fixture "dee2-DRP003416.zip")

        let stubBytes (url: string) : Async<byte[]> =
            async {
                if url.Contains "huge" then return zipBytes
                else return failwith "no paper pdf"
            }

        let opts =
            { CrawlOptions.Default with
                Fetch = stubText
                FetchBytes = stubBytes
                Log = Log.silent
                ThrottleMs = 0 }

        try
            let summary =
                Crawler.crawlAllWithAsync opts "PRJDB5192" outDir (Crawler.PaperFrom "PMC000000") (Crawler.Dee2Discover "athaliana")
                |> Async.RunSynchronously

            Assert.Equal(PaperResult.NotFound, summary.Paper)
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``LIVE crawlAll of a small project with paper and DEE2 (opt-in via INSDC_LIVE_TESTS=1)`` () =
        if System.Environment.GetEnvironmentVariable "INSDC_LIVE_TESTS" = "1" then
            let outDir = Path.Combine(Path.GetTempPath(), $"r2all-live-{Guid.NewGuid():N}")

            try
                // `PaperDiscover` rather than a hardcoded id: the full-text endpoints
                // are keyed on a PMCID, and this test previously passed a DOI (which
                // 404s). Discovery resolves whatever the record actually links.
                let summary =
                    Crawler.crawlAll "PRJDB5192" outDir Crawler.PaperDiscover (Crawler.Dee2Discover "athaliana")

                Assert.True(summary.InsdcCounts.["runs"] > 0)
            finally
                if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

// ---- Resolve layer + R1 crawler tests (DEE2 archive + paper; no INSDC XML) ----

/// Shared stub for the resolve-layer and R1 crawler tests.
module private R1TestHelpers =

    /// Crawl options serving the whole R1 surface (discovery, the BioProject and
    /// Study records, the DEE2 search + bundle, both paper renditions), plus the
    /// URLs the crawl actually requested. `paperJats`/`paperPdf` gate which
    /// full-text formats EuropePMC is pretending to hold.
    ///
    /// The recorded URLs are the point: `Fetch.entity` *swallows* a failed batch
    /// (it logs `Failed` and yields no records), so a `failwith` stub alone could
    /// not prove that a crawl skips the sample/experiment/run fetches — the failure
    /// would be silently absorbed and the test would pass regardless. Asserting on
    /// what was *requested* is the only honest check.
    let stubR1 (paperJats: bool) (paperPdf: bool) : CrawlOptions * System.Collections.Concurrent.ConcurrentBag<string> =
        let requested = System.Collections.Concurrent.ConcurrentBag<string>()

        let stubText (url: string) : Async<string> =
            async {
                requested.Add url

                if url.Contains "filereport" then
                    return TestFiles.fixtureText "crawl-PRJDB5192.filereport.tsv"
                // The DEE2 search URL carries the study accession too
                // (…&accessionsearch=DRP003416), so match it before the
                // accession-keyed INSDC branches below.
                elif url.Contains "search2.sh" then
                    return TestFiles.fixtureText "dee2-search-DRP003416.html"
                elif url.Contains "fullTextXML" then
                    if paperJats then
                        return TestFiles.fixtureText "paper-PRJDB5192.jats.xml"
                    else
                        return failwith "no open-access JATS"
                elif url.Contains "DRP003416" then
                    return TestFiles.fixtureText "DRP003416.xml"
                elif url.Contains "PRJDB5192" then
                    return TestFiles.fixtureText "PRJDB5192.xml"
                else
                    return failwithf "unexpected URL: %s" url
            }

        let zipBytes = File.ReadAllBytes(TestFiles.fixture "dee2-DRP003416.zip")

        let stubBytes (url: string) : Async<byte[]> =
            async {
                requested.Add url

                if url.Contains "huge" then
                    return zipBytes
                // PDFs come from the PMC Open Access bucket, not EuropePMC.
                elif url.Contains "pmc-oa-opendata" then
                    if paperPdf then
                        return File.ReadAllBytes(TestFiles.fixture "paper-PRJDB5192.pdf")
                    else
                        return failwith "no PDF"
                else
                    return failwithf "unexpected binary URL: %s" url
            }

        let options =
            { CrawlOptions.Default with
                Fetch = stubText
                FetchBytes = stubBytes
                Log = Log.silent
                ThrottleMs = 0 }

        options, requested

/// The middle layer: an INSDC accession -> the ids EuropePMC and DEE2 key on.
/// These exist so a caller can compose the pieces without an orchestrator — which
/// is the whole point of having them public.
type CrawlerResolveTests() =

    [<Fact>]
    member _.``resolve fetches the BioProject and Study, and nothing below them`` () =
        let opts, requested = R1TestHelpers.stubR1 true true
        let refs = Crawler.resolveWithAsync opts "PRJDB5192" |> Async.RunSynchronously

        Assert.Equal("PRJDB5192", refs.Accession)
        Assert.Equal(1, refs.BioProjects.Length)
        Assert.Equal(1, refs.Studies.Length)

        // A paper lookup and a DEE2 lookup never read a sample/experiment/run, so
        // resolve must not pay for them (thousands of requests on a big project).
        let urls = requested |> Seq.toList

        Assert.False(
            urls
            |> List.exists (fun (u: string) ->
                u.Contains "SAMD00064197" || u.Contains "DRX066772" || u.Contains "DRR072834"),
            "resolve must not fetch sample/experiment/run records")

    [<Fact>]
    member _.``dee2Key is the archive-assigned Study accession`` () =
        let opts, _ = R1TestHelpers.stubR1 true true
        let refs = Crawler.resolveWithAsync opts "PRJDB5192" |> Async.RunSynchronously

        // DEE2 keys its bundles on the Study's `Accession` (DRP003416), never on the
        // submitter `Alias` — which for a GEO-origin study is a GSE id DEE2 has never
        // heard of. This is the rule that used to be an inline Array.tryHead.
        Assert.Equal<string option>(Some "DRP003416", Crawler.dee2Key refs)

    [<Fact>]
    member _.``paperRefs is empty when the records carry only housekeeping links`` () =
        let opts, _ = R1TestHelpers.stubR1 true true
        let refs = Crawler.resolveWithAsync opts "PRJDB5192" |> Async.RunSynchronously

        // The PRJDB5192/DRP003416 records link no publication (only ENA housekeeping
        // xrefs), which is exactly why `PaperDiscover` yields NotFound for them.
        Assert.Empty(Crawler.paperRefs refs)

    [<Fact>]
    member _.``compose: a DEE2 bundle from an INSDC accession, with no orchestrator`` () =
        // "I want DEE2 data discovered via INSDC accession mapping" — three ordinary
        // calls, no bespoke crawl function. This is what the resolve layer buys.
        let outDir = Path.Combine(Path.GetTempPath(), $"compose-dee2-{Guid.NewGuid():N}")
        let opts, _ = R1TestHelpers.stubR1 true true

        try
            let refs = Crawler.resolveWithAsync opts "PRJDB5192" |> Async.RunSynchronously

            let landed =
                match Crawler.dee2Key refs with
                | Some studyAccession ->
                    Dee2.crawlDee2WithAsync opts "athaliana" studyAccession outDir
                    |> Async.RunSynchronously
                | None -> None

            match landed with
            | Some path ->
                Assert.EndsWith("DRP003416.zip", path)
                Assert.True(File.Exists path)
            | None -> Assert.Fail "expected the DEE2 bundle to land"
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``compose: a paper in a chosen format from a PMCID already in hand`` () =
        // "I have a PMCID" — straight to the fetcher; no discovery, no INSDC call.
        let outDir = Path.Combine(Path.GetTempPath(), $"compose-paper-{Guid.NewGuid():N}")
        let opts, requested = R1TestHelpers.stubR1 true true

        try
            let result =
                Paper.crawlPaperFormatWithAsync opts PaperFormat.Jats "PMC123456" outDir
                |> Async.RunSynchronously

            match result with
            | PaperResult.JatsXml path -> Assert.True(File.Exists path)
            | other -> Assert.Fail(sprintf "expected JatsXml, got %A" other)

            // No ENA traffic at all: an id in hand needs no resolve step.
            let urls = requested |> Seq.toList
            Assert.DoesNotContain(urls, fun (u: string) -> u.Contains "filereport")
            Assert.DoesNotContain(urls, fun (u: string) -> u.Contains "browser/api/xml")
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

type R1CrawlerTests() =

    /// True when `outDir` holds an INSDC record XML — a BioProject/Study file at
    /// the root, or any of the R2 entity subfolders. R1 must produce none of
    /// these. Deliberately does not glob `*.xml`: the JATS paper is written as
    /// `<id>.jats.xml` and would match such a glob.
    let hasInsdcXml (outDir: string) : bool =
        let recordAtRoot =
            Directory.Exists outDir
            && Directory.GetFiles(outDir, "*.xml")
               |> Array.exists (fun f ->
                   let name = Path.GetFileName f
                   name = "PRJDB5192.xml" || name = "DRP003416.xml")

        recordAtRoot
        || [ "samples"; "experiments"; "runs" ]
           |> List.exists (fun folder -> Directory.Exists(Path.Combine(outDir, folder)))

    [<Fact>]
    member _.``R1A lands the DEE2 archive and the paper as JATS — and no PDF`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r1a-{Guid.NewGuid():N}")
        let opts, _ = R1TestHelpers.stubR1 true true

        try
            let summary =
                Crawler.crawlR1WithAsync opts Crawler.R1A "PRJDB5192" outDir (Crawler.PaperFrom "PMC123456") (Crawler.Dee2Discover "athaliana")
                |> Async.RunSynchronously

            match summary.Paper with
            | PaperResult.JatsXml path ->
                Assert.EndsWith("PMC123456.jats.xml", path)
                Assert.True(File.Exists path)
            | other -> Assert.Fail(sprintf "expected JatsXml, got %A" other)

            match summary.Dee2Path with
            | Some path ->
                Assert.EndsWith("DRP003416.zip", path)
                Assert.True(File.Exists path)
            | None -> Assert.Fail "expected the DEE2 bundle, got None"

            // R1A ships JATS only — the PDF must not be fetched alongside it.
            Assert.Empty(Directory.GetFiles(Path.Combine(outDir, "paper"), "*.pdf"))
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``R1B lands the DEE2 archive and the paper as PDF — and no JATS`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r1b-{Guid.NewGuid():N}")
        let opts, _ = R1TestHelpers.stubR1 true true

        try
            let summary =
                Crawler.crawlR1WithAsync opts Crawler.R1B "PRJDB5192" outDir (Crawler.PaperFrom "PMC123456") (Crawler.Dee2Discover "athaliana")
                |> Async.RunSynchronously

            match summary.Paper with
            | PaperResult.Pdf path ->
                Assert.EndsWith("PMC123456.pdf", path)
                Assert.True(File.Exists path)
            | other -> Assert.Fail(sprintf "expected Pdf, got %A" other)

            Assert.True(Option.isSome summary.Dee2Path)

            // R1B ships the PDF only — even though the JATS stub above would have
            // succeeded, a forced-PDF crawl must never fetch it.
            Assert.Empty(Directory.GetFiles(Path.Combine(outDir, "paper"), "*.jats.xml"))
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``R1 writes no INSDC XML and never fetches sample, experiment or run records`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r1-lean-{Guid.NewGuid():N}")
        let opts, requested = R1TestHelpers.stubR1 true true

        try
            Crawler.crawlR1WithAsync opts Crawler.R1A "PRJDB5192" outDir (Crawler.PaperFrom "PMC123456") (Crawler.Dee2Discover "athaliana")
            |> Async.RunSynchronously
            |> ignore

            Assert.False(hasInsdcXml outDir, "R1 must not write any INSDC record XML")

            // The lean-fetch property: R1 needs only the BioProject (publication
            // xrefs) and the Study (the DEE2 lookup key). Pulling the rest would
            // cost thousands of wasted requests on a large project.
            let urls = requested |> Seq.toList

            let leaked =
                urls
                |> List.filter (fun u ->
                    u.Contains "SAMD00064197" || u.Contains "DRX066772" || u.Contains "DRR072834")

            Assert.True(
                List.isEmpty leaked,
                sprintf "R1 must not fetch sample/experiment/run records, but requested: %A" leaked)

            // ...while still fetching the two records it does need. Pinned to the
            // Browser API endpoint on purpose: the bare accessions also appear in
            // the filereport URL (PRJDB5192) and the DEE2 search URL (DRP003416),
            // so matching on the accession alone would pass even if neither record
            // was ever fetched.
            let fetchedRecord (accession: string) =
                urls
                |> List.exists (fun (u: string) -> u.Contains "browser/api/xml" && u.Contains accession)

            Assert.True(fetchedRecord "PRJDB5192", "R1 must fetch the BioProject record (its publication xrefs)")
            Assert.True(fetchedRecord "DRP003416", "R1 must fetch the Study record (the DEE2 lookup key)")
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``R1A with no open-access JATS still lands the DEE2 archive`` () =
        // The two artifacts fail independently — a missing paper must not cost
        // us the counts. Note the PDF *is* available here, and must still not be
        // used: R1A means JATS or nothing.
        let outDir = Path.Combine(Path.GetTempPath(), $"r1a-nojats-{Guid.NewGuid():N}")
        let opts, _ = R1TestHelpers.stubR1 false true

        try
            let summary =
                Crawler.crawlR1WithAsync opts Crawler.R1A "PRJDB5192" outDir (Crawler.PaperFrom "PMC999999") (Crawler.Dee2Discover "athaliana")
                |> Async.RunSynchronously

            Assert.Equal(PaperResult.NotFound, summary.Paper)
            Assert.False(Directory.Exists(Path.Combine(outDir, "paper")))

            match summary.Dee2Path with
            | Some path -> Assert.True(File.Exists path)
            | None -> Assert.Fail "the DEE2 bundle must land even when the paper does not"
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``Dee2Skip fetches no DEE2 bundle`` () =
        let outDir = Path.Combine(Path.GetTempPath(), $"r1-nodee2-{Guid.NewGuid():N}")
        let opts, _ = R1TestHelpers.stubR1 true true

        try
            let summary =
                Crawler.crawlR1WithAsync opts Crawler.R1A "PRJDB5192" outDir (Crawler.PaperFrom "PMC123456") Crawler.Dee2Skip
                |> Async.RunSynchronously

            Assert.Equal<string option>(None, summary.Dee2Path)
            Assert.False(Directory.Exists(Path.Combine(outDir, "counts")))
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``PaperSkip fetches no paper — a thing the old option API could not say`` () =
        // Under the previous signature, `paperId = None` meant "auto-discover", so
        // there was NO way to ask for a crawl without a paper. `PaperSkip` is that
        // missing case: even with a PMCID resolvable and both renditions on offer,
        // no EuropePMC request is made.
        let outDir = Path.Combine(Path.GetTempPath(), $"r1-nopaper-{Guid.NewGuid():N}")
        let opts, requested = R1TestHelpers.stubR1 true true

        try
            let summary =
                Crawler.crawlR1WithAsync opts Crawler.R1A "PRJDB5192" outDir Crawler.PaperSkip (Crawler.Dee2Discover "athaliana")
                |> Async.RunSynchronously

            Assert.Equal(PaperResult.NotFound, summary.Paper)
            Assert.False(Directory.Exists(Path.Combine(outDir, "paper")))

            let urls = requested |> Seq.toList
            Assert.DoesNotContain(urls, fun (u: string) -> u.Contains "fullText")
            Assert.DoesNotContain(urls, fun (u: string) -> u.Contains "europepmc")

            // ...and the DEE2 archive still lands.
            Assert.True(Option.isSome summary.Dee2Path)
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``crawlR1Formats fetches everything but the paper exactly once for both formats`` () =
        // The reason `crawlR1Formats` exists. R1A and R1B differ ONLY in the paper's
        // file format, so crawling them together must not repeat ENA discovery, the
        // record fetches, the PMCID resolution or the DEE2 download — the second
        // format should cost exactly one extra request (its paper).
        let outDir = Path.Combine(Path.GetTempPath(), $"r1-both-{Guid.NewGuid():N}")
        let opts, requested = R1TestHelpers.stubR1 true true

        try
            let summaries =
                Crawler.crawlR1FormatsWithAsync
                    opts
                    [ Crawler.R1A; Crawler.R1B ]
                    "PRJDB5192"
                    outDir
                    (Crawler.PaperFrom "PMC123456")
                    (Crawler.Dee2Discover "athaliana")
                |> Async.RunSynchronously

            Assert.Equal(2, List.length summaries)

            let urls = requested |> Seq.toList
            let count (fragment: string) = urls |> List.filter (fun (u: string) -> u.Contains fragment) |> List.length

            // Fetched once, shared by both formats.
            Assert.Equal(1, count "filereport") // ENA discovery
            Assert.Equal(2, count "browser/api/xml") // the BioProject + Study records (one batch each)
            Assert.Equal(1, count "search2.sh") // DEE2 bundle lookup
            Assert.Equal(1, count "huge") // DEE2 zip download

            // Fetched per format — and only the paper. Note the two renditions come
            // from two DIFFERENT services: JATS from EuropePMC, the PDF from the PMC
            // Open Access bucket (EuropePMC serves no usable PDF).
            Assert.Equal(1, count "fullTextXML") // R1A
            Assert.Equal(1, count "pmc-oa-opendata") // R1B

            // Both renditions land in the one outDir, ready to be split into two trees.
            let paperDir = Path.Combine(outDir, "paper")
            Assert.Equal(1, Directory.GetFiles(paperDir, "*.jats.xml").Length)
            Assert.Equal(1, Directory.GetFiles(paperDir, "*.pdf").Length)

            // The archive is shared: every summary points at the same single zip.
            let dee2Paths = summaries |> List.map (fun s -> s.Dee2Path) |> List.distinct
            Assert.Equal(1, List.length dee2Paths)
            Assert.True(dee2Paths |> List.forall Option.isSome, "the shared DEE2 archive should have landed")

            // ...and each summary carries its own format's paper.
            match summaries with
            | [ a; b ] ->
                Assert.Equal(Crawler.R1A, a.Format)
                Assert.Equal(Crawler.R1B, b.Format)

                match a.Paper, b.Paper with
                | PaperResult.JatsXml _, PaperResult.Pdf _ -> ()
                | other -> Assert.Fail(sprintf "expected (JatsXml, Pdf), got %A" other)
            | other -> Assert.Fail(sprintf "expected two summaries, got %A" other)
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)

    [<Fact>]
    member _.``R1 with paperId None auto-discovers — the fixture has no xref, so no paper`` () =
        // paperId = None routes through `Paper.discoverPmcidWithAsync`. The
        // PRJDB5192/DRP003416 fixtures carry only ENA housekeeping links (no
        // PUBMED/PMC), so discovery finds nothing and no EuropePMC call is made.
        let outDir = Path.Combine(Path.GetTempPath(), $"r1-discover-{Guid.NewGuid():N}")
        let opts, requested = R1TestHelpers.stubR1 true true

        try
            let summary =
                Crawler.crawlR1WithAsync opts Crawler.R1A "PRJDB5192" outDir Crawler.PaperDiscover (Crawler.Dee2Discover "athaliana")
                |> Async.RunSynchronously

            Assert.Equal(PaperResult.NotFound, summary.Paper)
            Assert.False(Directory.Exists(Path.Combine(outDir, "paper")))

            let urls = requested |> Seq.toList
            Assert.DoesNotContain(urls, fun (u: string) -> u.Contains "fullText")

            // The DEE2 archive is unaffected by the paper miss.
            Assert.True(Option.isSome summary.Dee2Path)
        finally
            if Directory.Exists outDir then Directory.Delete(outDir, recursive = true)
