namespace BioFSharp.INSDC.Tests

open System
open System.IO
open System.Threading
open Xunit

open BioFSharp.INSDC.Crawler

type CrawlerHardeningTests() =

    [<Fact>]
    member _.``retry does not retry cancellation`` () =
        let mutable calls = 0
        let fetch (_: string) =
            async {
                calls <- calls + 1
                return raise (OperationCanceledException("cancelled"))
            }

        Assert.ThrowsAny<OperationCanceledException>(fun () ->
            BioFSharp.INSDC.Crawler.Internal.Http.withRetry 5 Log.silent fetch "stub://cancel"
            |> Async.RunSynchronously
            |> ignore)
        |> ignore

        Assert.Equal(1, calls)

    [<Theory>]
    [<InlineData("")>]
    [<InlineData("not\ta\tfilereport\nvalue")>]
    [<InlineData("run_accession\texperiment_accession\tsample_accession\tstudy_accession\tsecondary_study_accession\nRUN")>]
    member _.``malformed discovery responses are rejected`` (body: string) =
        Assert.Throws<FormatException>(fun () -> Discovery.parse body |> ignore)
        |> ignore

    [<Fact>]
    member _.``conflicting discovery parents are rejected`` () =
        let body =
            "run_accession\texperiment_accession\tsample_accession\tstudy_accession\tsecondary_study_accession\n"
            + "RUN1\tEXP1\tSAMPLE1\tPROJECT1\tSTUDY1\n"
            + "RUN2\tEXP1\tSAMPLE2\tPROJECT1\tSTUDY2\n"

        let error = Assert.Throws<FormatException>(fun () -> Discovery.parse body |> ignore)
        Assert.Contains("conflicting", error.Message)

    [<Fact>]
    member _.``record batch failure is fatal by default and opt-in partial otherwise`` () =
        let fetch (url: string) =
            async {
                if url.StartsWith("stub://portal", StringComparison.Ordinal) then
                    return TestFiles.fixtureText "crawl-PRJDB5192.filereport.tsv"
                elif url.Contains("PRJDB5192") then
                    return TestFiles.fixtureText "PRJDB5192.xml"
                else
                    return raise (InvalidOperationException("upstream failed"))
            }

        let strict =
            { CrawlOptions.Default with
                PortalBaseUrl = "stub://portal"
                BrowserBaseUrl = "stub://browser"
                Retries = 0
                ThrottleMs = 0
                Fetch = fetch
                Log = Log.silent }

        let error =
            Assert.Throws<InvalidOperationException>(fun () ->
                Crawler.crawlWithAsync strict "PRJDB5192" |> Async.RunSynchronously |> ignore)

        Assert.Contains("Incomplete", error.Message)

        let partial = { strict with ContinueOnPartialFailure = true }
        let result = Crawler.crawlWithAsync partial "PRJDB5192" |> Async.RunSynchronously
        Assert.Single(result.BioProjects) |> ignore
        Assert.Empty(result.Runs)

    [<Fact>]
    member _.``paper resume reuses valid JATS without a fetch`` () =
        let outDir = Path.Combine(Path.GetTempPath(), "insdc-paper-resume-" + Guid.NewGuid().ToString("N"))
        let paperDir = Path.Combine(outDir, Paper.PaperFolder)
        Directory.CreateDirectory(paperDir) |> ignore
        let expectedPath = Path.Combine(paperDir, "PMC1.jats.xml")
        File.WriteAllText(expectedPath, "<article><front/></article>")
        let mutable calls = 0

        let options =
            { CrawlOptions.Default with
                Fetch = fun _ -> async { calls <- calls + 1; return raise (Exception("must not fetch")) }
                Log = Log.silent }

        try
            let result = Paper.crawlPaperFormatWithAsync options PaperFormat.Jats "PMC1" outDir |> Async.RunSynchronously
            Assert.Equal(PaperResult.JatsXml expectedPath, result)
            Assert.Equal(0, calls)
        finally
            if Directory.Exists(outDir) then Directory.Delete(outDir, true)

    [<Fact>]
    member _.``malformed PDF and ZIP payloads never land at final paths`` () =
        let outDir = Path.Combine(Path.GetTempPath(), "insdc-malformed-artifacts-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(outDir) |> ignore

        let paperOptions =
            { CrawlOptions.Default with
                Retries = 0
                FetchBytes = fun _ -> async { return [| 1uy; 2uy; 3uy |] }
                Log = Log.silent }

        let dee2Options =
            { paperOptions with
                Fetch = fun _ -> async { return "<a href=https://example.test/bundle.zip>zip</a>" } }

        try
            let paper = Paper.crawlPaperFormatWithAsync paperOptions PaperFormat.Pdf "PMC1" outDir |> Async.RunSynchronously
            let bundle = Dee2.crawlDee2WithAsync dee2Options "athaliana" "DRP1" outDir |> Async.RunSynchronously

            Assert.Equal(PaperResult.NotFound, paper)
            Assert.True(bundle.IsNone)
            Assert.False(File.Exists(Path.Combine(outDir, Paper.PaperFolder, "PMC1.pdf")))
            Assert.False(File.Exists(Path.Combine(outDir, Dee2.CountsFolder, "DRP1.zip")))
            Assert.Empty(Directory.GetFiles(outDir, "*.tmp", SearchOption.AllDirectories))
        finally
            if Directory.Exists(outDir) then Directory.Delete(outDir, true)
