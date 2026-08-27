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

    let assertQualifiedOutputsResolve directory stateName graph report =
        let statePath = Path.Combine(directory, stateName)
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
            assertQualifiedOutputsResolve directory "project-state.arcir.json" graph accounted.Accounting |> ignore

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
            assertQualifiedOutputsResolve directory "study-state.arcir.json" graph accounted.Accounting |> ignore

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

    [<Fact>]
    member _.``BioSample Experiment and Run account their complete connected fixture chain`` () =
        let sample = BioSample.read (TestFiles.fixture "SAMD00064197.xml") |> Seq.exactlyOne
        let experiment = Experiment.read (TestFiles.fixture "DRX066772.xml") |> Seq.exactlyOne
        let run = Run.read (TestFiles.fixture "DRR072834.xml") |> Seq.exactlyOne
        let sampleXml = BioSample.writeString sample
        let experimentXml = Experiment.writeString experiment
        let runXml = Run.writeString run

        let find (selectorPart: string) (report: FieldAccountingReport) =
            report.Entries
            |> List.find (fun entry -> entry.Input.Selector.Value.Contains(selectorPart))

        let expectEmitted (entry: FieldAccountingEntry) =
            match entry.Outcome with
            | FieldAccountingOutcome.Emitted outputs -> outputs
            | outcome -> failwithf "Expected emitted field, got %A" outcome

        let expectIgnored (entry: FieldAccountingEntry) =
            match entry.Outcome with
            | FieldAccountingOutcome.Ignored reason -> Assert.False(String.IsNullOrWhiteSpace reason)
            | outcome -> failwithf "Expected intentionally ignored field, got %A" outcome

        let expectUnsupported (report: FieldAccountingReport) (entry: FieldAccountingEntry) =
            match entry.Outcome with
            | FieldAccountingOutcome.Unsupported diagnosticId ->
                Assert.Contains(entry.Input, report.Diagnostics.Diagnostics.[diagnosticId].Targets)
            | outcome -> failwithf "Expected unsupported field, got %A" outcome

        withTempDirectory (fun directory ->
            let _, sampleBytes, sampleRevision = writeSource directory "sample.xml" sampleXml
            let _, experimentBytes, experimentRevision = writeSource directory "experiment.xml" experimentXml
            let _, runBytes, runRevision = writeSource directory "run.xml" runXml
            let accountedSample = INSDC.bioSampleWithAccounting sampleRevision sample
            let accountedExperiment = INSDC.experimentWithAccounting experimentRevision experiment
            let accountedRun = INSDC.runWithAccounting runRevision run

            Assert.Equal(accountedSample, INSDC.bioSampleWithAccounting sampleRevision sample)
            Assert.Equal(accountedExperiment, INSDC.experimentWithAccounting experimentRevision experiment)
            Assert.Equal(accountedRun, INSDC.runWithAccounting runRevision run)

            let graph =
                [ accountedSample.Conversion
                  accountedExperiment.Conversion
                  accountedRun.Conversion ]
                |> INSDC.build

            assertCompleteAccounting
                sampleXml
                sampleBytes
                sampleRevision
                (BioSample.xpathEntries sample |> Array.length)
                graph
                accountedSample.Accounting

            assertCompleteAccounting
                experimentXml
                experimentBytes
                experimentRevision
                (Experiment.xpathEntries experiment |> Array.length)
                graph
                accountedExperiment.Accounting

            assertCompleteAccounting
                runXml
                runBytes
                runRevision
                (Run.xpathEntries run |> Array.length)
                graph
                accountedRun.Accounting

            let statePath = Path.Combine(directory, "connected-state.arcir.json")
            let stateRevision = ArcIRJson.writeNew statePath graph |> expectOk

            for report in
                [ accountedSample.Accounting
                  accountedExperiment.Accounting
                  accountedRun.Accounting ] do
                for binding in FieldAccounting.qualifyEmitted stateRevision report do
                    for output in binding.Outputs do
                        ArcIRJson.resolveFragment output |> expectOk |> ignore

            let taxonOutputs =
                find "/SAMPLE_NAME/TAXON_ID" accountedSample.Accounting |> expectEmitted

            Assert.True(taxonOutputs |> List.exists (function ArcJsonLocation.PropertyValue _ -> true | _ -> false))
            Assert.True(taxonOutputs |> List.exists (function ArcJsonLocation.Relation _ -> true | _ -> false))

            find "/DESIGN/DESIGN_DESCRIPTION" accountedExperiment.Accounting |> expectIgnored
            find "/STUDY_REF/@accession" accountedExperiment.Accounting |> expectEmitted |> ignore
            find "/STUDY_REF/@refname" accountedExperiment.Accounting |> expectIgnored

            find "/SAMPLE_DESCRIPTOR/IDENTIFIERS/EXTERNAL_ID[1]/text()" accountedExperiment.Accounting
            |> expectEmitted
            |> ignore

            find "/SAMPLE_DESCRIPTOR/@accession" accountedExperiment.Accounting |> expectIgnored
            find "/PLATFORM/ILLUMINA/INSTRUMENT_MODEL" accountedExperiment.Accounting
            |> expectEmitted
            |> ignore

            find "/SPOT_DESCRIPTOR/SPOT_DECODE_SPEC/READ_SPEC[1]/READ_CLASS" accountedExperiment.Accounting
            |> expectUnsupported accountedExperiment.Accounting

            find "/EXPERIMENT_REF/@accession" accountedRun.Accounting |> expectEmitted |> ignore
            find "/EXPERIMENT_REF/@refcenter" accountedRun.Accounting |> expectIgnored

            find "/RUN_LINKS/RUN_LINK[1]/XREF_LINK/DB" accountedRun.Accounting
            |> expectUnsupported accountedRun.Accounting)

    [<Fact>]
    member _.``Analysis Submission and Receipt account every fixture leaf`` () =
        let analysis = Analysis.read (TestFiles.fixture "ERZ496533.xml") |> Seq.exactlyOne
        let submission = Submission.read (TestFiles.fixture "DRA005154.xml") |> Seq.exactlyOne
        let receipt = Receipt.read (TestFiles.fixture "receipt-sample.xml")
        let analysisXml = Analysis.writeString analysis
        let submissionXml = Submission.writeString submission
        let receiptXml = Receipt.writeString receipt

        let find (selectorPart: string) (report: FieldAccountingReport) =
            report.Entries
            |> List.find (fun entry -> entry.Input.Selector.Value.Contains(selectorPart))

        let expectEmitted (entry: FieldAccountingEntry) =
            match entry.Outcome with
            | FieldAccountingOutcome.Emitted outputs -> outputs
            | outcome -> failwithf "Expected emitted field, got %A" outcome

        let expectIgnored (entry: FieldAccountingEntry) =
            match entry.Outcome with
            | FieldAccountingOutcome.Ignored reason -> Assert.False(String.IsNullOrWhiteSpace reason)
            | outcome -> failwithf "Expected intentionally ignored field, got %A" outcome

        let expectUnsupported (report: FieldAccountingReport) (entry: FieldAccountingEntry) =
            match entry.Outcome with
            | FieldAccountingOutcome.Unsupported diagnosticId ->
                Assert.Contains(entry.Input, report.Diagnostics.Diagnostics.[diagnosticId].Targets)
            | outcome -> failwithf "Expected unsupported field, got %A" outcome

        withTempDirectory (fun directory ->
            let _, analysisBytes, analysisRevision = writeSource directory "analysis.xml" analysisXml
            let _, submissionBytes, submissionRevision = writeSource directory "submission.xml" submissionXml
            let _, receiptBytes, receiptRevision = writeSource directory "receipt.xml" receiptXml
            let accountedAnalysis = INSDC.analysisWithAccounting analysisRevision analysis
            let accountedSubmission = INSDC.submissionWithAccounting submissionRevision submission
            let accountedReceipt = INSDC.receiptWithAccounting receiptRevision receipt

            Assert.Equal(accountedAnalysis, INSDC.analysisWithAccounting analysisRevision analysis)
            Assert.Equal(accountedSubmission, INSDC.submissionWithAccounting submissionRevision submission)
            Assert.Equal(accountedReceipt, INSDC.receiptWithAccounting receiptRevision receipt)

            let graph =
                [ accountedAnalysis.Conversion
                  accountedSubmission.Conversion
                  accountedReceipt.Conversion ]
                |> INSDC.build

            assertCompleteAccounting
                analysisXml
                analysisBytes
                analysisRevision
                (Analysis.xpathEntries analysis |> Array.length)
                graph
                accountedAnalysis.Accounting

            assertCompleteAccounting
                submissionXml
                submissionBytes
                submissionRevision
                (Submission.xpathEntries submission |> Array.length)
                graph
                accountedSubmission.Accounting

            assertCompleteAccounting
                receiptXml
                receiptBytes
                receiptRevision
                (Receipt.xpathEntries receipt |> Array.length)
                graph
                accountedReceipt.Accounting

            let statePath = Path.Combine(directory, "administrative-state.arcir.json")
            let stateRevision = ArcIRJson.writeNew statePath graph |> expectOk

            for report in
                [ accountedAnalysis.Accounting
                  accountedSubmission.Accounting
                  accountedReceipt.Accounting ] do
                for binding in FieldAccounting.qualifyEmitted stateRevision report do
                    for output in binding.Outputs do
                        ArcIRJson.resolveFragment output |> expectOk |> ignore

            find "/ANALYSIS/FILES/FILE[1]/@filename" accountedAnalysis.Accounting
            |> expectEmitted
            |> ignore

            find "/ANALYSIS/STUDY_REF/@accession" accountedAnalysis.Accounting
            |> expectEmitted
            |> ignore

            find "/ANALYSIS/STUDY_REF/IDENTIFIERS/PRIMARY_ID" accountedAnalysis.Accounting
            |> expectIgnored

            find "/ANALYSIS_TYPE/SEQUENCE_VARIATION/PROGRAM" accountedAnalysis.Accounting
            |> expectUnsupported accountedAnalysis.Accounting

            find "/ANALYSIS_LINKS/ANALYSIS_LINK[1]/XREF_LINK/DB" accountedAnalysis.Accounting
            |> expectUnsupported accountedAnalysis.Accounting

            find "/SUBMISSION/@lab_name" accountedSubmission.Accounting
            |> expectEmitted
            |> fun outputs ->
                Assert.True(outputs |> List.exists (function ArcJsonLocation.Relation _ -> true | _ -> false))

            find "/SUBMISSION/SUBMISSION_LINKS/SUBMISSION_LINK[1]/XREF_LINK/DB" accountedSubmission.Accounting
            |> expectUnsupported accountedSubmission.Accounting

            find "/RECEIPT/SAMPLE[1]/@accession" accountedReceipt.Accounting
            |> expectEmitted
            |> ignore

            find "/RECEIPT/SAMPLE[1]/EXT_ID[1]/@accession" accountedReceipt.Accounting
            |> expectIgnored

            find "/RECEIPT/@submissionFile" accountedReceipt.Accounting
            |> expectEmitted
            |> fun outputs -> Assert.True(outputs |> List.exists (function ArcJsonLocation.Object _ -> true | _ -> false))

            find "/RECEIPT/MESSAGES/INFO[1]" accountedReceipt.Accounting
            |> expectUnsupported accountedReceipt.Accounting

            find "/RECEIPT/ACTIONS[1]" accountedReceipt.Accounting
            |> expectUnsupported accountedReceipt.Accounting)

    [<Fact>]
    member _.``JATS front matter accounting resolves source and ArcIR output designations`` () =
        let path = TestFiles.fixture "paper-PRJDB5192.jats.xml"
        let bytes = File.ReadAllBytes path
        let xml = Encoding.UTF8.GetString bytes
        let revision = ArtifactRevision.ofBytes path None bytes
        let accounted = Ingest.paperWithAccounting revision (Path.GetFileName path) bytes []
        let repeated = Ingest.paperWithAccounting revision (Path.GetFileName path) bytes []
        let graph = INSDC.build [ accounted.Conversion ]
        let sourceDocument = XPointer.entityDoc xml
        let outputLocations = ArcIRJson.locations graph |> Set.ofSeq

        Assert.Equal(accounted, repeated)
        Assert.NotEmpty accounted.Accounting.Entries
        Assert.Empty accounted.Accounting.Diagnostics.Diagnostics

        for entry in accounted.Accounting.Entries do
            Assert.Equal(revision, entry.Input.Artifact)
            Assert.Equal(F1Accounting.XPointerConformsTo, entry.Input.Selector.ConformsTo)
            Assert.True(
                XPointer.resolve sourceDocument entry.Input.Selector.Value |> Option.isSome,
                $"JATS selector did not resolve: {entry.Input.Selector.Value}"
            )

            match entry.Outcome with
            | FieldAccountingOutcome.Emitted outputs ->
                Assert.NotEmpty outputs
                for output in outputs do
                    Assert.Contains(output, outputLocations)
            | FieldAccountingOutcome.Ignored reason -> Assert.False(String.IsNullOrWhiteSpace reason)
            | outcome -> failwithf "Expected emitted or reviewed-ignored JATS metadata, got %A" outcome

        withTempDirectory (fun directory ->
            assertQualifiedOutputsResolve directory "paper-state.arcir.json" graph accounted.Accounting
            |> ignore)

        let title =
            accounted.Accounting.Entries
            |> List.find (fun entry -> entry.Input.Selector.Value.Contains("article-title"))

        match title.Outcome with
        | FieldAccountingOutcome.Emitted outputs ->
            Assert.True(outputs |> List.exists (function ArcJsonLocation.ObjectAnnotationValue _ -> true | _ -> false))
        | outcome -> failwithf "Expected the JATS title to be emitted, got %A" outcome

    [<Fact>]
    member _.``count header accounting uses byte-position cells and resolves every ArcIR output`` () =
        let path = TestFiles.fixture "counts-PRJDB5192.tsv"
        let bytes = File.ReadAllBytes path
        let revision = ArtifactRevision.ofBytes path None bytes
        let accounted = Ingest.countDataWithAccounting revision (Path.GetFileName path) bytes
        let repeated = Ingest.countDataWithAccounting revision (Path.GetFileName path) bytes
        let graph = INSDC.build [ accounted.Conversion ]
        let outputLocations = ArcIRJson.locations graph |> Set.ofSeq
        let selectorPattern =
            "^#selector\\(type=DataPositionSelector,start=(\\d+),end=(\\d+)\\)$"

        let selectedValue (entry: FieldAccountingEntry) =
            let matched =
                Text.RegularExpressions.Regex.Match(entry.Input.Selector.Value, selectorPattern)

            Assert.True(matched.Success, $"Invalid data-position selector: {entry.Input.Selector.Value}")
            let startPosition = Int32.Parse matched.Groups.[1].Value
            let endPosition = Int32.Parse matched.Groups.[2].Value
            Assert.InRange(startPosition, 0, bytes.Length)
            Assert.InRange(endPosition, startPosition, bytes.Length)
            Encoding.UTF8.GetString(bytes, startPosition, endPosition - startPosition)

        Assert.Equal(accounted, repeated)
        Assert.Equal(3, accounted.Accounting.Entries.Length)
        Assert.Empty accounted.Accounting.Diagnostics.Diagnostics

        for entry in accounted.Accounting.Entries do
            Assert.Equal(revision, entry.Input.Artifact)
            Assert.Equal(IngestAccounting.DataPositionSelectorConformsTo, entry.Input.Selector.ConformsTo)
            Assert.False(String.IsNullOrWhiteSpace(selectedValue entry))

            match entry.Outcome with
            | FieldAccountingOutcome.Emitted outputs ->
                Assert.NotEmpty outputs
                for output in outputs do
                    Assert.Contains(output, outputLocations)
            | FieldAccountingOutcome.Ignored reason -> Assert.False(String.IsNullOrWhiteSpace reason)
            | outcome -> failwithf "Expected emitted or reviewed-ignored count header cell, got %A" outcome

        let featureColumn =
            accounted.Accounting.Entries
            |> List.find (fun entry -> selectedValue entry = "gene_id")

        match featureColumn.Outcome with
        | FieldAccountingOutcome.Ignored _ -> ()
        | outcome -> failwithf "Expected the feature identifier column to be intentionally ignored, got %A" outcome

        let runColumn =
            accounted.Accounting.Entries
            |> List.find (fun entry -> selectedValue entry = "DRR072834")

        match runColumn.Outcome with
        | FieldAccountingOutcome.Emitted outputs ->
            Assert.True(outputs |> List.exists (function ArcJsonLocation.Relation _ -> true | _ -> false))
        | outcome -> failwithf "Expected the run header to emit the column and linkage, got %A" outcome

        withTempDirectory (fun directory ->
            assertQualifiedOutputsResolve directory "count-state.arcir.json" graph accounted.Accounting
            |> ignore)

        let mismatched = Array.copy bytes
        mismatched.[0] <- mismatched.[0] ^^^ 1uy

        Assert.Throws<ArgumentException>(fun () ->
            Ingest.countDataWithAccounting revision (Path.GetFileName path) mismatched |> ignore)
        |> ignore
