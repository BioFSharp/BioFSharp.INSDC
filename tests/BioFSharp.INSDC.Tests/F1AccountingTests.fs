namespace BioFSharp.INSDC.Tests

open System
open System.IO
open System.Text
open Xunit
open BioFSharp.ArcIR
open BioFSharp.IO.INSDC
open BioFSharp.INSDC.ArcIR

type F1AccountingTests() =

    let expectOk result =
        match result with
        | Ok value -> value
        | Error errors -> failwithf "Expected success, got %A" errors

    let withTempDirectory action =
        let directory = Path.Combine(Path.GetTempPath(), $"arcir-f1-{Guid.NewGuid():N}")
        Directory.CreateDirectory directory |> ignore

        try
            action directory
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    let writeSource (directory: string) (name: string) (xml: string) =
        let path = Path.Combine(directory, name)
        let bytes = UTF8Encoding(false).GetBytes xml
        File.WriteAllBytes(path, bytes)
        path, bytes, ArtifactRevision.ofBytes path None bytes

    let assertCompleteAccounting xml bytes revision expectedCount graph (report: FieldAccountingReport) =
        Assert.Equal(expectedCount, report.Entries.Length)

        Assert.Equal(
            report.Entries.Length,
            report.Entries |> List.map _.Input |> List.distinct |> List.length
        )

        Assert.True(ArtifactRevision.verifyBytes revision bytes)
        let document = XPointer.entityDoc xml
        let outputLocations = ArcIRJson.locations graph |> Set.ofSeq

        for entry in report.Entries do
            Assert.Equal(revision, entry.Input.Artifact)
            Assert.Equal(F1Accounting.XPointerConformsTo, entry.Input.Selector.ConformsTo)

            Assert.True(
                XPointer.resolve document entry.Input.Selector.Value |> Option.isSome,
                $"Source selector did not resolve: {entry.Input.Selector.Value}"
            )

            match entry.Outcome with
            | FieldAccountingOutcome.Emitted outputs ->
                Assert.NotEmpty outputs

                for output in outputs do
                    Assert.Contains(output, outputLocations)
            | FieldAccountingOutcome.Unsupported diagnosticId
            | FieldAccountingOutcome.Failed diagnosticId ->
                let diagnostic = report.Diagnostics.Diagnostics.[diagnosticId]
                Assert.Contains(entry.Input, diagnostic.Targets)
            | FieldAccountingOutcome.Ignored reason -> Assert.False(String.IsNullOrWhiteSpace reason)

    let assertQualifiedOutputsResolve directory graph report =
        let statePath = Path.Combine(directory, "state.arcir.json")
        let stateRevision = ArcIRJson.writeNew statePath graph |> expectOk
        let bindings = FieldAccounting.qualifyEmitted stateRevision report

        for binding in bindings do
            for output in binding.Outputs do
                ArcIRJson.resolveFragment output |> expectOk |> ignore

        bindings, stateRevision

    [<Fact>]
    member _.``BioProject accounts every source leaf and resolves every designation`` () =
        let project = BioProject.read (TestFiles.fixture "PRJDB5192.xml") |> Seq.exactlyOne
        let xml = BioProject.writeString project

        withTempDirectory (fun directory ->
            let _, bytes, sourceRevision = writeSource directory "project.xml" xml
            let accounted = INSDC.bioProjectWithAccounting sourceRevision project
            let repeated = INSDC.bioProjectWithAccounting sourceRevision project
            let graph = INSDC.build [ accounted.Conversion ]
            let expectedCount = BioProject.xpathEntries project |> Array.length

            Assert.Equal(accounted, repeated)
            assertCompleteAccounting xml bytes sourceRevision expectedCount graph accounted.Accounting
            assertQualifiedOutputsResolve directory graph accounted.Accounting |> ignore

            let title =
                accounted.Accounting.Entries
                |> List.find (fun entry -> entry.Input.Selector.Value.EndsWith("/TITLE)"))

            match title.Outcome with
            | FieldAccountingOutcome.Emitted outputs
                when outputs |> List.exists (function ArcJsonLocation.ObjectAnnotationValue _ -> true | _ -> false) -> ()
            | outcome -> failwithf "Expected the project title value to be emitted, got %A" outcome

            let unsupported =
                accounted.Accounting.Entries
                |> List.find (fun entry -> entry.Input.Selector.Value.Contains("/ORGANISM/STRAIN"))

            match unsupported.Outcome with
            | FieldAccountingOutcome.Unsupported diagnosticId ->
                Assert.Equal(DiagnosticSeverity.Warning, accounted.Accounting.Diagnostics.Diagnostics.[diagnosticId].Severity)
            | outcome -> failwithf "Expected the organism strain to be diagnosed, got %A" outcome)

    [<Fact>]
    member _.``Study accounts every source leaf and resolves every designation`` () =
        let study = Study.read (TestFiles.fixture "DRP003416.xml") |> Seq.exactlyOne
        let xml = Study.writeString study

        withTempDirectory (fun directory ->
            let _, bytes, sourceRevision = writeSource directory "study.xml" xml
            let accounted = INSDC.studyWithAccounting sourceRevision study
            let graph = INSDC.build [ accounted.Conversion ]
            let expectedCount = Study.xpathEntries study |> Array.length

            assertCompleteAccounting xml bytes sourceRevision expectedCount graph accounted.Accounting
            assertQualifiedOutputsResolve directory graph accounted.Accounting |> ignore

            let title =
                accounted.Accounting.Entries
                |> List.find (fun entry -> entry.Input.Selector.Value.EndsWith("/STUDY_TITLE)"))

            match title.Outcome with
            | FieldAccountingOutcome.Emitted outputs
                when outputs |> List.exists (function ArcJsonLocation.ObjectAnnotationValue _ -> true | _ -> false) -> ()
            | outcome -> failwithf "Expected the study title value to be emitted, got %A" outcome)

    [<Fact>]
    member _.``base mapping links an XML title occurrence to its additive ARC companion`` () =
        let project = BioProject.read (TestFiles.fixture "PRJDB5192.xml") |> Seq.exactlyOne
        let xml = BioProject.writeString project
        let claims =
            TestFiles.fixtureText "sssom/INSDCER-ARC.sssom.tsv"
            |> SssomMapping.loadEmbedded
            |> SssomMapping.exactMatches

        withTempDirectory (fun directory ->
            let _, _, sourceRevision = writeSource directory "project.xml" xml
            let accounted = INSDC.bioProjectWithAccounting sourceRevision project
            let sourceGraph = INSDC.build [ accounted.Conversion ]

            let mapped =
                SemanticMapping.applyClaims claims sourceGraph |> expectOk

            let titleEntry =
                accounted.Accounting.Entries
                |> List.find (fun entry -> entry.Input.Selector.Value.EndsWith("/TITLE)"))

            let titleLocations =
                match titleEntry.Outcome with
                | FieldAccountingOutcome.Emitted locations -> locations
                | outcome -> failwithf "Expected title outputs, got %A" outcome

            let application =
                mapped.Applications
                |> List.find (fun value -> List.contains value.Input titleLocations)

            let statePath = Path.Combine(directory, "mapped-state.arcir.json")
            let stateRevision = ArcIRJson.writeNew statePath mapped.Graph |> expectOk
            let sourceOutput = ArcIRJson.fragmentRef stateRevision application.Input
            let mappedOutput = ArcIRJson.fragmentRef stateRevision application.Output

            Assert.True(XPointer.resolve (XPointer.entityDoc xml) titleEntry.Input.Selector.Value |> Option.isSome)
            ArcIRJson.resolveFragment sourceOutput |> expectOk |> ignore
            ArcIRJson.resolveFragment mappedOutput |> expectOk |> ignore
            Assert.NotEqual(application.Input, application.Output))
