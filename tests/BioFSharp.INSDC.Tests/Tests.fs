namespace BioFSharp.INSDC.Tests

open System
open System.Collections
open System.IO
open System.Reflection
open Xunit

open BioFSharp.FileFormats.INSDC
open BioFSharp.IO.INSDC

module private TestFiles =

    let fixture fileName =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "fixtures", fileName))

    let fixtureText fileName =
        File.ReadAllText(fixture fileName)

    let roundtrip read write value =
        let filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xml")

        try
            write filePath value
            read filePath |> Seq.exactlyOne
        finally
            if File.Exists filePath then
                File.Delete filePath

module private ObjectGraph =

    let private isSimple (t: Type) =
        t.IsPrimitive
        || t.IsEnum
        || t = typeof<string>
        || t = typeof<decimal>
        || t = typeof<DateTime>
        || t = typeof<Guid>

    let private asSequence (value: obj) =
        (value :?> IEnumerable)
        |> Seq.cast<obj>
        |> Seq.toArray

    let rec private diff path (expected: obj) (actual: obj) =
        if Object.ReferenceEquals(expected, actual) then
            None
        elif isNull expected || isNull actual then
            Some $"{path}: expected {expected}, got {actual}"
        else
            let expectedType = expected.GetType()
            let actualType = actual.GetType()

            if expectedType <> actualType then
                Some $"{path}: expected type {expectedType.FullName}, got {actualType.FullName}"
            elif isSimple expectedType then
                if expected.Equals(actual) then
                    None
                else
                    Some $"{path}: expected {expected}, got {actual}"
            elif typeof<IEnumerable>.IsAssignableFrom(expectedType) && expectedType <> typeof<string> then
                let expectedItems = asSequence expected
                let actualItems = asSequence actual

                if expectedItems.Length <> actualItems.Length then
                    Some $"{path}: expected {expectedItems.Length} items, got {actualItems.Length}"
                else
                    Seq.zip expectedItems actualItems
                    |> Seq.mapi (fun i (left, right) -> diff $"{path}[{i}]" left right)
                    |> Seq.tryPick id
            else
                expectedType.GetProperties(BindingFlags.Instance ||| BindingFlags.Public)
                |> Array.filter (fun p -> p.CanRead && p.GetIndexParameters().Length = 0)
                |> Array.sortBy (fun property -> property.Name)
                |> Array.tryPick (fun property ->
                    let expectedValue = property.GetValue(expected)
                    let actualValue = property.GetValue(actual)
                    diff $"{path}.{property.Name}" expectedValue actualValue)

    let equal expected actual =
        // Roundtrip tests compare the generated object graph instead of raw XML,
        // keeping them stable if serializer attribute ordering changes.
        match diff "$" (box expected) (box actual) with
        | Some message -> Assert.True(false, message)
        | None -> ()

module private Assertions =

    let attributeValue tag (attributes: seq<BioFSharp.FileFormats.INSDC.Attribute>) =
        attributes
        |> Seq.find (fun attribute -> attribute.Tag = tag)
        |> fun attribute -> attribute.Value

module private XPointer =

    open System.Xml

    /// Strip the XPointer wrapper: "#xpointer(/PROJECT/NAME)" -> "/PROJECT/NAME".
    let xpath (selector: string) =
        let openParen = selector.IndexOf('(')
        selector.Substring(openParen + 1, selector.Length - openParen - 2)

    /// Load a fixture and return a document rooted at the single entity element. The fixtures wrap
    /// the entity in a `*_SET`, while the selectors are absolute from the entity root (`/PROJECT`).
    let entityDoc (xml: string) =
        let outer = XmlDocument()
        outer.LoadXml(xml)
        let root = outer.DocumentElement

        let entity =
            if root.Name.EndsWith("_SET") then
                root.ChildNodes
                |> Seq.cast<XmlNode>
                |> Seq.find (fun n -> n.NodeType = XmlNodeType.Element)
            else
                root :> XmlNode

        let doc = XmlDocument()
        doc.LoadXml(entity.OuterXml)
        doc

    /// Resolve a selector to the text/value of the node it points at (None if it matches nothing).
    let resolve (doc: XmlDocument) (selector: string) : string option =
        match doc.SelectSingleNode(xpath selector) with
        | null -> None
        | node -> Some node.InnerText

type BioProjectTests() =

    [<Fact>]
    member _.``Read BioProject fixture values`` () =
        let project = BioProject.read (TestFiles.fixture "PRJDB5192.xml") |> Seq.exactlyOne

        Assert.Equal("PRJDB5192", project.Accession)
        Assert.Equal("DRP003416", project.Identifiers.SecondaryId |> Seq.head |> fun id -> id.Value)
        Assert.Equal("Arabidopsis thaliana strain:Col-0", project.Name)
        Assert.Equal("Arabidopsis thaliana", project.SubmissionProject.Organism.ScientificName)
        Assert.Equal("PUBLIC", project.ProjectAttributes |> Assertions.attributeValue "ENA-STATUS")

    [<Fact>]
    member _.``Roundtrip BioProject fixture through disk`` () =
        let project = BioProject.read (TestFiles.fixture "PRJDB5192.xml") |> Seq.exactlyOne
        let reparsed = TestFiles.roundtrip BioProject.read BioProject.write project

        ObjectGraph.equal project reparsed

    [<Fact>]
    member _.``BioProject read and readString parse the same value`` () =
        let fromFile = BioProject.read (TestFiles.fixture "PRJDB5192.xml") |> Seq.exactlyOne
        let fromString = BioProject.readString (TestFiles.fixtureText "PRJDB5192.xml") |> Seq.exactlyOne

        ObjectGraph.equal fromFile fromString

type StudyTests() =

    [<Fact>]
    member _.``Read Study fixture values`` () =
        let study = Study.read (TestFiles.fixture "DRP003416.xml") |> Seq.exactlyOne

        Assert.Equal("DRP003416", study.Accession)
        Assert.Equal("PRJDB5192", study.Identifiers.SecondaryId |> Seq.head |> fun id -> id.Value)
        Assert.Equal("The gene-body chromatin modifications dynamics mediates epigenome differentiation in Arabidopsis", study.Descriptor.StudyTitle)
        Assert.Equal("Arabidopsis thaliana strain:Col-0", study.Descriptor.CenterProjectName)
        Assert.Equal("PUBLIC", study.StudyAttributes |> Assertions.attributeValue "ENA-STATUS")

    [<Fact>]
    member _.``Roundtrip Study fixture through disk`` () =
        let study = Study.read (TestFiles.fixture "DRP003416.xml") |> Seq.exactlyOne
        let reparsed = TestFiles.roundtrip Study.read Study.write study

        ObjectGraph.equal study reparsed

    [<Fact>]
    member _.``Study read and readString parse the same value`` () =
        let fromFile = Study.read (TestFiles.fixture "DRP003416.xml") |> Seq.exactlyOne
        let fromString = Study.readString (TestFiles.fixtureText "DRP003416.xml") |> Seq.exactlyOne

        ObjectGraph.equal fromFile fromString

type BioSampleTests() =

    [<Fact>]
    member _.``Read BioSample fixture values`` () =
        let sample = BioSample.read (TestFiles.fixture "SAMD00064197.xml") |> Seq.exactlyOne

        Assert.Equal("SAMD00064197", sample.Accession)
        Assert.Equal("DRS039895", sample.Identifiers.SecondaryId |> Seq.head |> fun id -> id.Value)
        Assert.Equal("WT Col-0", sample.Title)
        Assert.Equal(3702, sample.SampleName.TaxonId)
        Assert.Equal("Arabidopsis thaliana", sample.SampleName.ScientificName)
        Assert.Equal("Col-0", sample.SampleAttributes |> Assertions.attributeValue "ecotype")

    [<Fact>]
    member _.``Roundtrip BioSample fixture through disk`` () =
        let sample = BioSample.read (TestFiles.fixture "SAMD00064197.xml") |> Seq.exactlyOne
        let reparsed = TestFiles.roundtrip BioSample.read BioSample.write sample

        ObjectGraph.equal sample reparsed

    [<Fact>]
    member _.``BioSample read and readString parse the same value`` () =
        let fromFile = BioSample.read (TestFiles.fixture "SAMD00064197.xml") |> Seq.exactlyOne
        let fromString = BioSample.readString (TestFiles.fixtureText "SAMD00064197.xml") |> Seq.exactlyOne

        ObjectGraph.equal fromFile fromString

type ExperimentTests() =

    [<Fact>]
    member _.``Read Experiment fixture values`` () =
        let experiment = Experiment.read (TestFiles.fixture "DRX066772.xml") |> Seq.exactlyOne

        Assert.Equal("DRX066772", experiment.Accession)
        Assert.Equal("WT_H3K4me3_2", experiment.Title)
        Assert.Equal("DRP003416", experiment.StudyRef.Accession)
        Assert.Equal("DRS039895", experiment.Design.SampleDescriptor.Accession)
        Assert.Equal(LibraryStrategy.ChIpSeq, experiment.Design.LibraryDescriptor.LibraryStrategy)
        Assert.Equal(IlluminaModel.IlluminaHiSeq4000, experiment.Platform.Illumina.InstrumentModel)

    [<Fact>]
    member _.``Roundtrip Experiment fixture through disk`` () =
        let experiment = Experiment.read (TestFiles.fixture "DRX066772.xml") |> Seq.exactlyOne
        let reparsed = TestFiles.roundtrip Experiment.read Experiment.write experiment

        ObjectGraph.equal experiment reparsed

    [<Fact>]
    member _.``Experiment read and readString parse the same value`` () =
        let fromFile = Experiment.read (TestFiles.fixture "DRX066772.xml") |> Seq.exactlyOne
        let fromString = Experiment.readString (TestFiles.fixtureText "DRX066772.xml") |> Seq.exactlyOne

        ObjectGraph.equal fromFile fromString

type RunTests() =

    [<Fact>]
    member _.``Read Run fixture values`` () =
        let run = Run.read (TestFiles.fixture "DRR072834.xml") |> Seq.exactlyOne

        Assert.Equal("DRR072834", run.Accession)
        Assert.Equal("WT_H3K4me3_2", run.Title)
        Assert.Equal("DRX066772", run.ExperimentRef.Accession)
        Assert.Equal("NIG", run.ExperimentRef.Refcenter)
        Assert.Equal("1037391408", run.RunAttributes |> Assertions.attributeValue "ENA-BASE-COUNT")
        Assert.Equal("20341008", run.RunAttributes |> Assertions.attributeValue "ENA-SPOT-COUNT")

    [<Fact>]
    member _.``Roundtrip Run fixture through disk`` () =
        let run = Run.read (TestFiles.fixture "DRR072834.xml") |> Seq.exactlyOne
        let reparsed = TestFiles.roundtrip Run.read Run.write run

        ObjectGraph.equal run reparsed

    [<Fact>]
    member _.``Run read and readString parse the same value`` () =
        let fromFile = Run.read (TestFiles.fixture "DRR072834.xml") |> Seq.exactlyOne
        let fromString = Run.readString (TestFiles.fixtureText "DRR072834.xml") |> Seq.exactlyOne

        ObjectGraph.equal fromFile fromString

type AnalysisTests() =

    [<Fact>]
    member _.``Read Analysis fixture values`` () =
        let analysis = Analysis.read (TestFiles.fixture "ERZ496533.xml") |> Seq.exactlyOne

        Assert.Equal("ERZ496533", analysis.Accession)
        Assert.Equal("DNA sequencing ACAN", analysis.Title)
        Assert.Equal("ERP107353", analysis.StudyRef.Accession)
        Assert.Equal(2, analysis.Files.Count)
        Assert.Equal("PUBLIC", analysis.AnalysisAttributes |> Assertions.attributeValue "ENA-STATUS")

    [<Fact>]
    member _.``Roundtrip Analysis fixture through disk`` () =
        let analysis = Analysis.read (TestFiles.fixture "ERZ496533.xml") |> Seq.exactlyOne
        let reparsed = TestFiles.roundtrip Analysis.read Analysis.write analysis

        ObjectGraph.equal analysis reparsed

    [<Fact>]
    member _.``Analysis read and readString parse the same value`` () =
        let fromFile = Analysis.read (TestFiles.fixture "ERZ496533.xml") |> Seq.exactlyOne
        let fromString = Analysis.readString (TestFiles.fixtureText "ERZ496533.xml") |> Seq.exactlyOne

        ObjectGraph.equal fromFile fromString

type SubmissionTests() =

    [<Fact>]
    member _.``Read Submission fixture values`` () =
        let submission = Submission.read (TestFiles.fixture "DRA005154.xml") |> Seq.exactlyOne

        Assert.Equal("DRA005154", submission.Accession)
        Assert.Equal("NIG", submission.CenterName)
        Assert.Equal("DDBJ", submission.BrokerName)
        Assert.Equal("Submitted by NIG on 28-JAN-2017", submission.Title)
        let firstLink = submission.SubmissionLinks |> Seq.head
        Assert.Equal("ENA-FASTQ-FILES", firstLink.XrefLink.Db)

    [<Fact>]
    member _.``Roundtrip Submission fixture through disk`` () =
        let submission = Submission.read (TestFiles.fixture "DRA005154.xml") |> Seq.exactlyOne
        let reparsed = TestFiles.roundtrip Submission.read Submission.write submission

        ObjectGraph.equal submission reparsed

    [<Fact>]
    member _.``Submission read and readString parse the same value`` () =
        let fromFile = Submission.read (TestFiles.fixture "DRA005154.xml") |> Seq.exactlyOne
        let fromString = Submission.readString (TestFiles.fixtureText "DRA005154.xml") |> Seq.exactlyOne

        ObjectGraph.equal fromFile fromString

type ReceiptTests() =

    [<Fact>]
    member _.``Read Receipt fixture values`` () =
        let receipt = Receipt.read (TestFiles.fixture "receipt-sample.xml")

        Assert.True(receipt.Success)
        Assert.Equal("submission.xml", receipt.SubmissionFile)
        Assert.Equal("ERA970284", receipt.Submission.Accession)
        let sample = receipt.Sample |> Seq.exactlyOne
        Assert.Equal("ERS1838367", sample.Accession)
        Assert.Equal("SAMEA104174130", sample.ExtId |> Seq.head |> fun ext -> ext.Accession)
        Assert.Contains(ReceiptActions.Add, receipt.Actions)

    [<Fact>]
    member _.``Roundtrip Receipt fixture through disk`` () =
        let original = Receipt.read (TestFiles.fixture "receipt-sample.xml")
        let filePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{System.Guid.NewGuid():N}.xml")
        try
            Receipt.write filePath original
            let reparsed = Receipt.read filePath
            ObjectGraph.equal original reparsed
        finally
            if System.IO.File.Exists filePath then System.IO.File.Delete filePath

    [<Fact>]
    member _.``Receipt read and readString parse the same value`` () =
        let fromFile = Receipt.read (TestFiles.fixture "receipt-sample.xml")
        let fromString = Receipt.readString (TestFiles.fixtureText "receipt-sample.xml")

        ObjectGraph.equal fromFile fromString

type FragmentSelectorTests() =

    // The generated `FragmentSelectors` maps live as static members on the FileFormats types;
    // fully qualify to disambiguate from the same-named `BioFSharp.IO.INSDC` modules.
    let bioProjectSelectors = BioFSharp.FileFormats.INSDC.BioProject.FragmentSelectors

    [<Fact>]
    member _.``BioProject selectors have the expected XPointer strings`` () =
        Assert.Equal("#xpointer(/PROJECT/NAME)", bioProjectSelectors.["Name"])
        Assert.Equal("#xpointer(/PROJECT/TITLE)", bioProjectSelectors.["Title"])
        Assert.Equal("#xpointer(/PROJECT/@accession)", bioProjectSelectors.["Accession"])
        Assert.Equal("#xpointer(/PROJECT/COLLABORATORS/COLLABORATOR)", bioProjectSelectors.["Collaborators"])
        Assert.Equal("#xpointer(/PROJECT/IDENTIFIERS/SECONDARY_ID/text())", bioProjectSelectors.["Identifiers.SecondaryId.Value"])
        Assert.Equal(
            "#xpointer(/PROJECT/SUBMISSION_PROJECT/ORGANISM/SCIENTIFIC_NAME)",
            bioProjectSelectors.["SubmissionProject.Organism.ScientificName"])

    [<Fact>]
    member _.``Every entity root exposes a non-empty selector map`` () =
        Assert.NotEmpty(BioFSharp.FileFormats.INSDC.BioProject.FragmentSelectors)
        Assert.NotEmpty(BioFSharp.FileFormats.INSDC.Study.FragmentSelectors)
        Assert.NotEmpty(BioFSharp.FileFormats.INSDC.BioSample.FragmentSelectors)
        Assert.NotEmpty(BioFSharp.FileFormats.INSDC.Experiment.FragmentSelectors)
        Assert.NotEmpty(BioFSharp.FileFormats.INSDC.Run.FragmentSelectors)
        Assert.NotEmpty(BioFSharp.FileFormats.INSDC.Analysis.FragmentSelectors)
        Assert.NotEmpty(BioFSharp.FileFormats.INSDC.Submission.FragmentSelectors)
        Assert.NotEmpty(BioFSharp.FileFormats.INSDC.Receipt.FragmentSelectors)

    [<Fact>]
    member _.``BioProject selectors resolve to the right nodes in a real fixture`` () =
        let project = BioProject.read (TestFiles.fixture "PRJDB5192.xml") |> Seq.exactlyOne
        let doc = XPointer.entityDoc (TestFiles.fixtureText "PRJDB5192.xml")
        let resolved key = (XPointer.resolve doc bioProjectSelectors.[key]).Value

        Assert.Equal(project.Accession, resolved "Accession")
        Assert.Equal(project.Name, resolved "Name")
        Assert.Equal(
            (project.Identifiers.SecondaryId |> Seq.head).Value,
            resolved "Identifiers.SecondaryId.Value")
        Assert.Equal(
            project.SubmissionProject.Organism.ScientificName,
            resolved "SubmissionProject.Organism.ScientificName")

type XPathLookupTests() =

    // Phase 2: per-instance, position-qualified XPointer lookup driven by a property quotation.
    let project = BioProject.read (TestFiles.fixture "PRJDB5192.xml") |> Seq.exactlyOne

    [<Fact>]
    member _.``xpathOf resolves a scalar element`` () =
        Assert.Equal("/PROJECT/NAME", project |> BioProject.xpathOf <@ fun b -> b.Name @>)

    [<Fact>]
    member _.``xpathOf resolves an attribute`` () =
        Assert.Equal("/PROJECT/@accession", project |> BioProject.xpathOf <@ fun b -> b.Accession @>)

    [<Fact>]
    member _.``xpathOf resolves a nested element path`` () =
        Assert.Equal(
            "/PROJECT/SUBMISSION_PROJECT/ORGANISM/SCIENTIFIC_NAME",
            project |> BioProject.xpathOf <@ fun b -> b.SubmissionProject.Organism.ScientificName @>)

    [<Fact>]
    member _.``xpathOf injects a 1-based positional predicate for a collection item`` () =
        Assert.Equal(
            "/PROJECT/IDENTIFIERS/SECONDARY_ID[1]/text()",
            project |> BioProject.xpathOf <@ fun b -> b.Identifiers.SecondaryId.[0].Value @>)

    [<Fact>]
    member _.``xpointerOf wraps the xpath as a W3C XPointer fragment`` () =
        let selector = <@ fun (b: BioProject) -> b.Name @>
        Assert.Equal("#xpointer(/PROJECT/NAME)", project |> BioProject.xpointerOf selector)
        Assert.Equal(
            "#xpointer(" + (project |> BioProject.xpathOf selector) + ")",
            project |> BioProject.xpointerOf selector)

    [<Fact>]
    member _.``xpointerOf selectors resolve to the value read from the document`` () =
        let doc = XPointer.entityDoc (TestFiles.fixtureText "PRJDB5192.xml")
        let resolve selector = (XPointer.resolve doc selector).Value

        Assert.Equal(project.Name, resolve (project |> BioProject.xpointerOf <@ fun b -> b.Name @>))
        Assert.Equal(project.Accession, resolve (project |> BioProject.xpointerOf <@ fun b -> b.Accession @>))
        Assert.Equal(
            (project.Identifiers.SecondaryId |> Seq.head).Value,
            resolve (project |> BioProject.xpointerOf <@ fun b -> b.Identifiers.SecondaryId.[0].Value @>))

    [<Fact>]
    member _.``xpathOf raises on an out-of-range collection index`` () =
        Assert.ThrowsAny<exn>(fun () ->
            project |> BioProject.xpathOf <@ fun b -> b.Identifiers.SecondaryId.[99].Value @> |> ignore)
        |> ignore

type XPathEntriesTests() =

    // Phase 3: serializable per-instance DTO — every present leaf with its positional XPath + value.
    let project = BioProject.read (TestFiles.fixture "PRJDB5192.xml") |> Seq.exactlyOne
    let entries = BioProject.xpathEntries project
    let byPath path = entries |> Array.find (fun e -> e.Path = path)

    [<Fact>]
    member _.``xpathEntries emits scalar, attribute, and positional collection leaves`` () =
        let name = byPath "Name"
        Assert.Equal("/PROJECT/NAME", name.XPath)
        Assert.Equal(project.Name, name.Value)

        let accession = byPath "Accession"
        Assert.Equal("/PROJECT/@accession", accession.XPath)
        Assert.Equal(project.Accession, accession.Value)

        let secondaryId = byPath "Identifiers.SecondaryId[0].Value"
        Assert.Equal("/PROJECT/IDENTIFIERS/SECONDARY_ID[1]/text()", secondaryId.XPath)
        Assert.Equal((project.Identifiers.SecondaryId |> Seq.head).Value, secondaryId.Value)

    [<Fact>]
    member _.``xpathEntries values resolve to the emitted xpath in the document`` () =
        let doc = XPointer.entityDoc (TestFiles.fixtureText "PRJDB5192.xml")

        for path in [ "Name"; "Accession"; "Identifiers.SecondaryId[0].Value" ] do
            let entry = byPath path
            let node = doc.SelectSingleNode(entry.XPath)
            Assert.NotNull(node)
            Assert.Equal(entry.Value, node.InnerText)

    [<Fact>]
    member _.``xpathEntries leaf xpaths are unique and non-empty`` () =
        Assert.NotEmpty(entries)
        Assert.All(entries, fun e -> Assert.False(System.String.IsNullOrEmpty e.XPath))
        Assert.Equal(entries.Length, entries |> Array.distinctBy (fun e -> e.XPath) |> Array.length)
