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

type DecompileTests() =

    // Phase 4: decompile a parsed record into structural-ontology (term, value) pairs.
    let project = BioProject.read (TestFiles.fixture "PRJDB5192.xml") |> Seq.exactlyOne
    let decompiled = BioProject.decompile project
    let byXPath xpath = decompiled |> List.find (fun d -> d.XPath = xpath)

    [<Fact>]
    member _.``decompile couples scalar and attribute leaves with their ontology terms`` () =
        let name = byXPath "/PROJECT/NAME"
        Assert.Equal("BioProject.Name", name.Term.Name)
        Assert.Equal(project.Name, name.Value)

        let accession = byXPath "/PROJECT/@accession"
        Assert.Equal("BioProject.Accession", accession.Term.Name)
        Assert.Equal(project.Accession, accession.Value)

    [<Fact>]
    member _.``decompile collapses a positional collection leaf onto its structural term`` () =
        let secondaryId = byXPath "/PROJECT/IDENTIFIERS/SECONDARY_ID[1]/text()"
        Assert.Equal("BioProject.Identifiers.SecondaryId.Value", secondaryId.Term.Name)
        Assert.Equal((project.Identifiers.SecondaryId |> Seq.head).Value, secondaryId.Value)

    [<Fact>]
    member _.``decompile covers every leaf that xpathEntries emits`` () =
        let entries = BioProject.xpathEntries project
        Assert.Equal(entries.Length, decompiled.Length)
        let decompiledXPaths = decompiled |> List.map (fun d -> d.XPath) |> Set.ofList
        for e in entries do
            Assert.True(decompiledXPaths.Contains e.XPath, $"no ontology term for leaf {e.XPath}")

    [<Fact>]
    member _.``leaf terms part_of resolve up to the entity root`` () =
        let onto = StructuralOntology.ontology ()
        let byId = onto.Terms |> List.map (fun t -> t.Id, t) |> Map.ofList
        let parentOf (term: OboTerm) =
            term.Relationships
            |> List.tryPick (fun r ->
                if r.StartsWith "part_of " then Some byId.[r.Substring("part_of ".Length)] else None)
        let rec climb (term: OboTerm) fuel =
            if fuel = 0 then failwith "part_of chain did not terminate at a root"
            match parentOf term with
            | Some parent -> climb parent (fuel - 1)
            | None -> term
        let leaf = (byXPath "/PROJECT/SUBMISSION_PROJECT/ORGANISM/SCIENTIFIC_NAME").Term
        Assert.Equal("BioProject", (climb leaf 64).Name)

    [<Fact>]
    member _.``the structural ontology loads and resolves an xpath to a term`` () =
        Assert.NotEmpty((StructuralOntology.ontology ()).Terms)
        match StructuralOntology.tryTermForXPath "/PROJECT/NAME" with
        | Some term -> Assert.Equal("BioProject.Name", term.Name)
        | None -> Assert.True(false, "no term for /PROJECT/NAME")

    [<Fact>]
    member _.``decompile resolves cross-entity (BioSample) xpaths`` () =
        let sample = BioSample.read (TestFiles.fixture "SAMD00064197.xml") |> Seq.exactlyOne
        let sampleDecompiled = BioSample.decompile sample
        Assert.NotEmpty(sampleDecompiled)
        Assert.Equal((BioSample.xpathEntries sample).Length, sampleDecompiled.Length)
        let accession = sampleDecompiled |> List.find (fun d -> d.XPath = "/SAMPLE/@accession")
        Assert.Equal("BioSample.Accession", accession.Term.Name)
        Assert.Equal(sample.Accession, accession.Value)

    [<Fact>]
    member _.``leaf term names are entity-qualified, mirroring the full xpath`` () =
        // Regression: the entity is the first xpath segment and must lead the term name. Without it,
        // the Webin-wrapped copy of a field (`/WEBIN/EXPERIMENT/...`) collapses onto the same name as
        // the field on the standalone Experiment record (`/EXPERIMENT/...`).
        match StructuralOntology.tryTermForXPath "/WEBIN/EXPERIMENT/PLATFORM/GENAPSYS/INSTRUMENT_MODEL" with
        | Some term -> Assert.Equal("Webin.Experiment.Platform.Genapsys.InstrumentModel", term.Name)
        | None -> Assert.True(false, "no term for /WEBIN/EXPERIMENT/PLATFORM/GENAPSYS/INSTRUMENT_MODEL")

    [<Fact>]
    member _.``every decompiled leaf name is rooted at its entity`` () =
        Assert.All(decompiled, fun d -> Assert.StartsWith("BioProject.", d.Term.Name))

    [<Fact>]
    member _.``structural ontology term names are globally unique`` () =
        // The entity prefix is what keeps the parallel Webin / standalone hierarchies from colliding.
        let names = (StructuralOntology.ontology ()).Terms |> List.map (fun t -> t.Name)
        Assert.Equal(names.Length, names |> List.distinct |> List.length)

    [<Fact>]
    member _.``wrapped-collection leaves keep their item level in the term name`` () =
        // Regression: xscgen collapses the <PROJECT_ATTRIBUTES><PROJECT_ATTRIBUTE> wrapper+item into a
        // single `ProjectAttributes` property, so the property path is `ProjectAttributes.Tag`. The
        // ontology must still expose the item (`Attribute`) level — Tag is a field of an attribute, not
        // of the attributes collection — so the structural xpath maps to ...Attribute.Tag.
        match StructuralOntology.tryTermForXPath "/PROJECT/PROJECT_ATTRIBUTES/PROJECT_ATTRIBUTE/TAG" with
        | Some term -> Assert.Equal("BioProject.ProjectAttributes.Attribute.Tag", term.Name)
        | None -> Assert.True(false, "no term for /PROJECT/PROJECT_ATTRIBUTES/PROJECT_ATTRIBUTE/TAG")

    [<Fact>]
    member _.``unwrapped repeated elements are not given a spurious item level`` () =
        // Contrast with the wrapped case: SECONDARY_ID is an unwrapped repeated element (xscgen names
        // the property after the item), so no `Attribute`-style level must be spliced in.
        match StructuralOntology.tryTermForXPath "/PROJECT/IDENTIFIERS/SECONDARY_ID/text()" with
        | Some term -> Assert.Equal("BioProject.Identifiers.SecondaryId.Value", term.Name)
        | None -> Assert.True(false, "no term for /PROJECT/IDENTIFIERS/SECONDARY_ID/text()")

    [<Fact>]
    member _.``a decompiled wrapped-collection leaf resolves to the spliced item-level term`` () =
        let tag =
            decompiled
            |> List.find (fun d -> d.XPath = "/PROJECT/PROJECT_ATTRIBUTES/PROJECT_ATTRIBUTE[1]/TAG")
        Assert.Equal("BioProject.ProjectAttributes.Attribute.Tag", tag.Term.Name)


type ArcMappingTests() =

    // Phase 5: map the 8 IO-readable INSDC entities into the ArcIR property graph. Typed converters build
    // the structure (typed values, sub-objects, edges via the controlled Vocabulary); the structural-
    // ontology decompilation supplies the annotation overlay.
    let read reader file = reader (TestFiles.fixture file) |> Seq.exactlyOne
    let project = read BioProject.read "PRJDB5192.xml"
    let study = read Study.read "DRP003416.xml"
    let sample = read BioSample.read "SAMD00064197.xml"
    let experiment = read Experiment.read "DRX066772.xml"
    let run = read Run.read "DRR072834.xml"
    let analysis = read Analysis.read "ERZ496533.xml"
    let submission = read Submission.read "DRA005154.xml"
    let receipt = Receipt.read (TestFiles.fixture "receipt-sample.xml")

    let ir =
        [ INSDC.bioProject project
          INSDC.study study
          INSDC.bioSample sample
          INSDC.experiment experiment
          INSDC.run run
          INSDC.analysis analysis
          INSDC.submission submission
          INSDC.receipt receipt ]
        |> INSDC.build

    let objectById (id: string) = ir.Objects.[ArcId.Create id]
    let outgoing (id: string) = ArcIR.outgoing (ArcId.Create id) ir |> List.ofSeq
    let hasEdge subject predicate object' =
        outgoing subject |> List.exists (fun r -> r.Predicate = predicate && r.Object = ArcId.Create object')
    let hasPredicate subject predicate = outgoing subject |> List.exists (fun r -> r.Predicate = predicate)
    let byDType dtype = ir.Objects.Values |> Seq.filter (fun o -> o.DTypes.Contains dtype) |> List.ofSeq

    [<Fact>]
    member _.``every entity maps to a node keyed by its accession, with the right kind`` () =
        Assert.Equal(ArcObjectKind.Collection, (objectById project.Accession).Kind)
        Assert.Equal(ArcObjectKind.Collection, (objectById study.Accession).Kind)
        Assert.Equal(ArcObjectKind.Observable, (objectById sample.Accession).Kind)
        Assert.Equal(ArcObjectKind.Activity, (objectById experiment.Accession).Kind)
        Assert.Equal(ArcObjectKind.Activity, (objectById run.Accession).Kind)
        Assert.Equal(ArcObjectKind.Activity, (objectById analysis.Accession).Kind)
        Assert.Equal(ArcObjectKind.Collection, (objectById submission.Accession).Kind)

    [<Fact>]
    member _.``DTypes and predicates use the controlled vocabulary IRIs`` () =
        Assert.True((objectById project.Accession).DTypes.Contains Vocabulary.DType.bioProject)
        Assert.True(hasPredicate experiment.Accession Vocabulary.Rel.hasStudy)

    [<Fact>]
    member _.``the sample organism is a deduped taxon node with a typed integer TaxonId`` () =
        // Mapping from the typed objects (not the flat string decompilation) is the point: TAXON_ID lands
        // as an ArcValue.Integer, and the taxon node id is shared across every sample of that organism.
        let organism = objectById "taxon:3702"
        Assert.Equal(ArcValue.Integer 3702L, organism.Properties.[Iri.Create "TaxonId"])
        Assert.True(hasEdge sample.Accession Vocabulary.Rel.hasOrganism "taxon:3702")

    [<Fact>]
    member _.``the experiment links to study, sample, instrument and protocol`` () =
        Assert.True(hasEdge experiment.Accession Vocabulary.Rel.hasStudy "DRP003416")
        // The SAMPLE_DESCRIPTOR references the SRA accession DRS039895, but the edge resolves to the
        // BioSample node (SAMD00064197) via the descriptor's EXTERNAL_ID[namespace=BioSample].
        Assert.True(hasEdge experiment.Accession Vocabulary.Rel.hasSample sample.Accession)
        Assert.True(hasPredicate experiment.Accession Vocabulary.Rel.usesInstrument)
        Assert.True(hasPredicate experiment.Accession Vocabulary.Rel.hasProtocol)

    [<Fact>]
    member _.``the run references its experiment`` () =
        Assert.True(hasEdge run.Accession Vocabulary.Rel.hasExperiment experiment.Accession)

    [<Fact>]
    member _.``the analysis references its study and produces data-file resources`` () =
        Assert.True(hasPredicate analysis.Accession Vocabulary.Rel.hasStudy)
        Assert.True(hasPredicate analysis.Accession Vocabulary.Rel.producesData)
        Assert.NotEmpty(byDType Vocabulary.DType.data)

    [<Fact>]
    member _.``the bioproject records a related-project edge`` () =
        Assert.True(hasPredicate project.Accession Vocabulary.Rel.hasParentProject)

    [<Fact>]
    member _.``the receipt acknowledges submitted objects and carries typed Success/ReceiptDate`` () =
        let node = byDType Vocabulary.DType.receipt |> List.exactlyOne
        Assert.True(hasPredicate node.Id.Value Vocabulary.Rel.acknowledges)
        let annValue name = node.Annotations |> List.pick (fun a -> if a.Property.Name = Some name then Some a.Value else None)
        match annValue "Success" with
        | AnnotationValue.Literal(ArcValue.Boolean _) -> ()
        | v -> Assert.True(false, $"Success should be a Boolean literal, got {v}")
        match annValue "ReceiptDate" with
        | AnnotationValue.Literal(ArcValue.DateTime _) -> ()
        | v -> Assert.True(false, $"ReceiptDate should be a DateTime literal, got {v}")

    [<Fact>]
    member _.``a shared institution collapses to one Agent node referenced by several entities`` () =
        let ddbj = ArcId.Create "org:ddbj"
        Assert.True(ir.Objects.ContainsKey ddbj)
        let referrers = ArcIR.incoming ddbj ir |> Seq.length
        Assert.True(referrers > 1, $"expected the shared org node to be referenced more than once, got {referrers}")

    [<Fact>]
    member _.``a closed-vocabulary enum maps to an ArcValue.Iri`` () =
        let instrument = byDType Vocabulary.DType.instrument |> List.head
        match instrument.Properties.[Iri.Create "InstrumentModel"] with
        | ArcValue.Iri _ -> ()
        | v -> Assert.True(false, $"InstrumentModel should be an Iri, got {v}")

    [<Fact>]
    member _.``mapped entity nodes are decoupled from the flat decompilation`` () =
        // Every entity now uses an explicit per-accession converter: annotations-first (empty Properties)
        // and no shredded structural-ontology leaves (those are entity-qualified, e.g. "BioSample.Foo.Bar").
        let cases =
            [ project.Accession, "BioProject"
              study.Accession, "Study"
              sample.Accession, "BioSample"
              experiment.Accession, "Experiment"
              run.Accession, "Run"
              analysis.Accession, "Analysis"
              submission.Accession, "Submission" ]

        for accession, prefix in cases do
            let node = objectById accession
            Assert.True(node.Properties.IsEmpty, $"{accession} should have empty Properties")
            Assert.NotEmpty node.Annotations
            Assert.False(
                node.Annotations |> List.exists (fun a -> (defaultArg a.Property.Name "").StartsWith(prefix + ".")),
                $"{accession} should carry no {prefix}.* decompilation leaves")

    [<Fact>]
    member _.``INSDC attributes become paired annotations, not tag-keyed properties`` () =
        // Arbitrary tag/value metadata belongs in the annotation layer, not the last-resort Properties
        // dump: the tag is the annotation term's Name and the value is its literal.
        let node = objectById analysis.Accession
        let ena = node.Annotations |> List.find (fun a -> a.Property.Name = Some "ENA-STATUS")
        match ena.Value with
        | AnnotationValue.Literal(ArcValue.String v) -> Assert.Equal("PUBLIC", v)
        | v -> Assert.True(false, $"expected a string literal, got {v}")
        // Not duplicated into Properties, and the redundant flat structural leaves are suppressed.
        Assert.False(node.Properties.ContainsKey(Iri.Create "ENA-STATUS"))
        Assert.False(
            node.Annotations
            |> List.exists (fun a -> (defaultArg a.Property.Name "").EndsWith ".Attribute.Tag"))


type ArcResolverTests() =

    // The resolve-relations-afterwards pass prefers an accession, then a refcenter-namespaced refname,
    // then a bare refname, then a synthetic id from the refname.
    let target =
        ArcObject.create
            "ACC1"
            ArcObjectKind.Collection
            []
            [ Iri.Create "Alias", ArcValue.String "myAlias"; Iri.Create "CenterName", ArcValue.String "CENTER" ]
            []

    let pending accession refname refcenter =
        { Subject = ArcId.Create "S"
          Predicate = Vocabulary.Rel.hasStudy
          TargetAccession = accession
          TargetRefname = refname
          TargetRefcenter = refcenter }

    [<Fact>]
    member _.``an accession resolves directly, even when the target record is not loaded`` () =
        let edges = Mapping.resolveRelations [] [ pending (Some "ACCX") None None ]
        Assert.Equal(ArcId.Create "ACCX", (List.exactlyOne edges).Object)

    [<Fact>]
    member _.``a refname resolves to a loaded object within its refcenter namespace`` () =
        let edges = Mapping.resolveRelations [ target ] [ pending None (Some "myAlias") (Some "CENTER") ]
        Assert.Equal(ArcId.Create "ACC1", (List.exactlyOne edges).Object)

    [<Fact>]
    member _.``an unresolved refname falls back to a synthetic id`` () =
        let edges = Mapping.resolveRelations [] [ pending None (Some "ghost") None ]
        Assert.Equal(ArcId.Create "ghost", (List.exactlyOne edges).Object)


type IdentifierAnnotationTests() =

    // Folding INSDC <IDENTIFIERS> composites into single annotations (+ an edge when the namespace names
    // a modelled entity), preserving object integrity instead of shredding namespace/value/label.
    let externalId (ns: string) (value: string) =
        let q = QualifiedName(Namespace = ns, Value = value)
        let ids = Identifier()
        ids.ExternalId.Add q
        ids

    [<Fact>]
    member _.``an external identifier folds to one namespaced annotation`` () =
        let anns, _ = Annotations.identifierAnnotations "SUBJ" (externalId "Study" "DRP999")
        let ann = anns |> List.find (fun a -> a.Property.Name = Some "Study")
        match ann.Value with
        | AnnotationValue.Literal(ArcValue.String v) -> Assert.Equal("DRP999", v)
        | v -> Assert.True(false, $"expected a string literal, got {v}")

    [<Fact>]
    member _.``an identifier naming a modelled entity draws a references edge`` () =
        let _, edges = Annotations.identifierAnnotations "SUBJ" (externalId "Study" "DRP999")
        let edge = List.exactlyOne edges
        Assert.Equal(ArcId.Create "SUBJ", edge.Subject)
        Assert.Equal(Vocabulary.Rel.references, edge.Predicate)
        Assert.Equal(Some "DRP999", edge.TargetAccession)

    [<Fact>]
    member _.``a self-referencing identifier folds but draws no edge`` () =
        let anns, edges = Annotations.identifierAnnotations "SUBJ" (externalId "BioProject" "SUBJ")
        Assert.NotEmpty anns
        Assert.Empty edges

    [<Fact>]
    member _.``an identifier in a non-modelled namespace draws no edge`` () =
        let _, edges = Annotations.identifierAnnotations "SUBJ" (externalId "PubMed" "12345")
        Assert.Empty edges


type IngestVocabularyTests() =

    // Lock the ingest vocabulary IRIs (house rule: vocabulary/term changes ship with a regression test).
    [<Fact>]
    member _.``ingest DTypes and predicates use stable controlled-vocabulary IRIs`` () =
        Assert.Equal("http://purl.org/arc/insdc#Publication", Vocabulary.DType.publication.Value)
        Assert.Equal("http://purl.org/arc/insdc#CountMatrix", Vocabulary.DType.countMatrix.Value)
        Assert.Equal("http://purl.org/arc/insdc#CountColumn", Vocabulary.DType.countColumn.Value)
        Assert.Equal("http://purl.org/arc/insdc#hasColumn", Vocabulary.Rel.hasColumn.Value)


type IngestPaperTests() =

    // Ingesting a paper: the file becomes a `publication` Resource, its authors become deduped person
    // Agents, and it links to the dataset it describes via `references` edges that resolve once the INSDC
    // record is present. See plans/arcir-ingest.md.
    let read reader file = reader (TestFiles.fixture file) |> Seq.exactlyOne
    let project = read BioProject.read "PRJDB5192.xml"
    let projectIr = [ INSDC.bioProject project ] |> INSDC.build

    let jats = TestFiles.fixture "paper-PRJDB5192.jats.xml"
    let meta = IngestReaders.readJats jats
    let ir = Ingest.incorporate projectIr [ Ingest.paperFromJats jats [ project.Accession ] ]

    let paperId = ArcId.Create "doi:10.1000/testgenomics.2017.001"
    let outgoing predicate = ArcIR.outgoing paperId ir |> Seq.filter (fun r -> r.Predicate = predicate) |> List.ofSeq

    [<Fact>]
    member _.``readJats extracts title, doi and both authors`` () =
        Assert.Equal(Some "Epigenetic regulation in Arabidopsis thaliana Col-0", meta.Title)
        Assert.Equal(Some "10.1000/testgenomics.2017.001", meta.Doi)
        Assert.Equal(2, meta.Authors.Length)
        let names = meta.Authors |> List.choose (fun a -> a.Name)
        Assert.Contains("Tetsuji Kakutani", names)
        Assert.Contains("Jane Doe", names)

    [<Fact>]
    member _.``the paper is a publication Resource keyed by its doi, annotations-first`` () =
        let node = ir.Objects.[paperId]
        Assert.Equal(ArcObjectKind.Resource, node.Kind)
        Assert.True(node.DTypes.Contains Vocabulary.DType.publication)
        // paper-level metadata lands in annotations (house style); file metadata in Properties.
        Assert.True(node.Annotations |> List.exists (fun a -> a.Property.Name = Some "Title"))
        Assert.True(node.Properties.ContainsKey(Iri.Create "Filename"))

    [<Fact>]
    member _.``each author is a person Agent linked by hasContact`` () =
        let contacts = outgoing Vocabulary.Rel.hasContact
        Assert.Equal(2, contacts.Length)
        for edge in contacts do
            Assert.True((ir.Objects.[edge.Object]).DTypes.Contains Vocabulary.DType.person)

    [<Fact>]
    member _.``the paper references the dataset, resolving onto the real project node`` () =
        let edge = outgoing Vocabulary.Rel.references |> List.exactlyOne
        Assert.Equal(ArcId.Create project.Accession, edge.Object)
        Assert.True(ir.Objects.ContainsKey edge.Object) // resolved, not dangling


type IngestAuthorMergeTests() =

    // An author whose email matches an existing contact's Agent id collapses to one enriched node
    // (merge-on-id): the paper enriches, rather than duplicates, a person already in the graph.
    let existingContact =
        ArcObject.create
            "agent:jane@example.org"
            ArcObjectKind.Agent
            [ Vocabulary.DType.agent; Vocabulary.DType.person ]
            [ Iri.Create "Organisation", ArcValue.String "Department of Integrative Genetics" ]
            []

    let paperResult =
        Ingest.paper
            { Title = Some "T"
              Doi = Some "10.1/x"
              Journal = None
              Authors = [ { Name = Some "Jane Doe"; Email = Some "jane@example.org"; Affiliation = None; Orcid = None } ] }
            { Name = "paper.pdf"; ByteSize = None; Checksum = None; MediaType = None }
            []

    let ir = Ingest.incorporate (ArcIR.Empty |> ArcIR.addObject existingContact) [ paperResult ]

    [<Fact>]
    member _.``a shared email collapses author and contact to one Agent with merged properties`` () =
        let node = ir.Objects.[ArcId.Create "agent:jane@example.org"]
        Assert.True(node.Properties.ContainsKey(Iri.Create "Organisation")) // from the pre-existing contact
        Assert.True(node.Properties.ContainsKey(Iri.Create "Name")) // from the paper author
        let personNodes = ir.Objects.Values |> Seq.filter (fun o -> o.DTypes.Contains Vocabulary.DType.person) |> Seq.length
        Assert.Equal(1, personNodes)


type IngestCountDataTests() =

    // Ingesting count data: the file is a `countMatrix` Resource; each run-accession column becomes a
    // `countColumn` fragment addressed by the RFC 7111 CSV selector `#col=<n>`, with a `producesData` edge
    // from its run (dangling until the run is merged). See plans/arcir-ingest.md.
    let tsv = IngestReaders.readCountFile (TestFiles.fixture "counts-PRJDB5192.tsv")
    let ir = [ Ingest.countData tsv ] |> INSDC.build
    let fileId = "count:counts-PRJDB5192.tsv"

    [<Fact>]
    member _.``the header parses to run-accession columns with 1-based positions`` () =
        Assert.Equal<CountColumn list>(
            [ { Index = 2; RunAccession = "DRR072834" }; { Index = 3; RunAccession = "DRR072835" } ],
            tsv.Columns)

    [<Fact>]
    member _.``the file is a countMatrix Resource with a countColumn fragment per run using RFC 7111 selectors`` () =
        Assert.True((ir.Objects.[ArcId.Create fileId]).DTypes.Contains Vocabulary.DType.countMatrix)
        let col2 = ir.Objects.[ArcId.Create(fileId + "#col=2")]
        Assert.True(col2.DTypes.Contains Vocabulary.DType.countColumn)
        Assert.Equal(ArcValue.String "#col=2", col2.Properties.[Iri.Create "FragmentSelector"])
        Assert.Equal(ArcValue.String "DRR072834", col2.Properties.[Iri.Create "RunAccession"])
        Assert.True(ir.Objects.ContainsKey(ArcId.Create(fileId + "#col=3")))
        Assert.True(
            ArcIR.outgoing (ArcId.Create fileId) ir
            |> Seq.exists (fun r -> r.Predicate = Vocabulary.Rel.hasColumn && r.Object = ArcId.Create(fileId + "#col=2")))

    [<Fact>]
    member _.``each column receives a producesData edge from its run`` () =
        Assert.True(
            ArcIR.outgoing (ArcId.Create "DRR072834") ir
            |> Seq.exists (fun r -> r.Predicate = Vocabulary.Rel.producesData && r.Object = ArcId.Create(fileId + "#col=2")))

    [<Fact>]
    member _.``the zip reader yields the same count file as the loose tsv`` () =
        let fromZip = IngestReaders.readCountArchive (TestFiles.fixture "counts-PRJDB5192.zip") |> List.exactlyOne
        Assert.Equal<CountFile>(tsv, fromZip)


type GraphMlExportTests() =

    // The GraphML serializer renders the assembled ArcIR property graph so it can be opened in Gephi
    // (nodes colored by kind, properties + annotations as inspectable columns, predicates as edge labels).
    let read reader file = reader (TestFiles.fixture file) |> Seq.exactlyOne
    let sample = read BioSample.read "SAMD00064197.xml"
    let experiment = read Experiment.read "DRX066772.xml"

    let ir =
        [ INSDC.bioProject (read BioProject.read "PRJDB5192.xml")
          INSDC.study (read Study.read "DRP003416.xml")
          INSDC.bioSample sample
          INSDC.experiment experiment
          INSDC.run (read Run.read "DRR072834.xml")
          INSDC.analysis (read Analysis.read "ERZ496533.xml")
          INSDC.submission (read Submission.read "DRA005154.xml")
          INSDC.receipt (Receipt.read (TestFiles.fixture "receipt-sample.xml")) ]
        |> INSDC.build

    let gml = XNamespace.Get "http://graphml.graphdrawing.org/xmlns"
    let doc = XDocument.Parse(GraphMl.toString ir)
    let attr name (el: XElement) = el.Attribute(XName.Get name).Value
    let nodesOf (d: XDocument) = d.Root.Descendants(gml + "node")
    let edgesOf (d: XDocument) = d.Root.Descendants(gml + "edge")
    let nodeById id = nodesOf doc |> Seq.find (fun n -> attr "id" n = id)
    let dataVal keyId (el: XElement) =
        el.Elements(gml + "data")
        |> Seq.tryFind (fun d -> attr "key" d = keyId)
        |> Option.map (fun d -> d.Value)
    let keyIdOf forWhat name =
        doc.Root.Elements(gml + "key")
        |> Seq.find (fun k -> attr "for" k = forWhat && attr "attr.name" k = name)
        |> attr "id"

    [<Fact>]
    member _.``the export is a well-formed graphml document`` () =
        Assert.Equal("graphml", doc.Root.Name.LocalName)
        Assert.Equal("directed", attr "edgedefault" (doc.Root.Element(gml + "graph")))

    [<Fact>]
    member _.``the node label prefers the accession over the title`` () =
        // The experiment has a Title, but its label is the accession (the stable identity).
        Assert.Equal(Some experiment.Accession, dataVal "label" (nodeById experiment.Accession))

    [<Fact>]
    member _.``node and edge counts match the graph (dangling endpoints become placeholder nodes)`` () =
        let missing =
            ir.Relations
            |> Seq.collect (fun r -> [ r.Subject; r.Object ])
            |> Seq.distinct
            |> Seq.filter (fun id -> not (ir.Objects.ContainsKey id))
            |> Seq.length
        Assert.Equal(ir.Objects.Count + missing, nodesOf doc |> Seq.length)
        Assert.Equal(ir.Relations.Count, edgesOf doc |> Seq.length)

    [<Fact>]
    member _.``a node carries its kind and a typed property renders in its own column`` () =
        Assert.Equal(Some "Activity", dataVal "kind" (nodeById experiment.Accession))
        // The deduped taxon node's typed-integer TaxonId lands in the `TaxonId` column.
        Assert.Equal(Some "3702", dataVal (keyIdOf "node" "TaxonId") (nodeById "taxon:3702"))

    [<Fact>]
    member _.``annotations are rendered as their own columns, not counted`` () =
        // The BioSample's `ecotype` attribute is an annotation; it must serialize as a populated node
        // column (keyed by the annotation term's name) rather than a bare count.
        let value = dataVal (keyIdOf "node" "ecotype") (nodeById sample.Accession)
        Assert.Equal(Some "Col-0", value)

    [<Fact>]
    member _.``an edge carries its predicate as the label`` () =
        let studyEdge =
            edgesOf doc
            |> Seq.find (fun e -> attr "source" e = experiment.Accession && attr "target" e = "DRP003416")
        Assert.Equal(Some "hasStudy", dataVal "predicate" studyEdge)

    [<Fact>]
    member _.``a relation to a missing target yields a placeholder node and a valid edge`` () =
        let node = ArcObject.create "A" ArcObjectKind.Collection [] [] []
        let relation = ArcRelation.create "A" Vocabulary.Rel.hasStudy "B" [] []
        let dangling = ArcIR.Empty |> ArcIR.addObject node |> ArcIR.addRelation relation
        let d = XDocument.Parse(GraphMl.toString dangling)
        let nodes = nodesOf d |> Seq.toList
        Assert.Equal(2, nodes.Length)
        let placeholder = nodes |> Seq.find (fun n -> attr "id" n = "B")
        Assert.Equal(Some "Missing", dataVal "kind" placeholder)
        Assert.Equal(1, edgesOf d |> Seq.length)


type HtmlExportTests() =

    // The interactive HTML viewer renders the same graph as a single self-contained page with an
    // embedded force-directed SVG and a click-to-inspect property/annotation panel.
    let read reader file = reader (TestFiles.fixture file) |> Seq.exactlyOne

    let ir =
        [ INSDC.bioProject (read BioProject.read "PRJDB5192.xml")
          INSDC.study (read Study.read "DRP003416.xml")
          INSDC.bioSample (read BioSample.read "SAMD00064197.xml")
          INSDC.experiment (read Experiment.read "DRX066772.xml")
          INSDC.run (read Run.read "DRR072834.xml")
          INSDC.analysis (read Analysis.read "ERZ496533.xml")
          INSDC.submission (read Submission.read "DRA005154.xml")
          INSDC.receipt (Receipt.read (TestFiles.fixture "receipt-sample.xml")) ]
        |> INSDC.build

    let html = Html.toString ir

    [<Fact>]
    member _.``the export is a self-contained html document with no external resources`` () =
        Assert.StartsWith("<!doctype html", html)
        Assert.Contains("<svg", html)
        Assert.Contains("const DATA =", html)
        // Everything is inline: no CDN script/stylesheet, no network dependency.
        Assert.DoesNotContain("<script src", html)
        Assert.DoesNotContain("<link", html)

    [<Fact>]
    member _.``node properties and rendered annotations are embedded for inspection`` () =
        // The deduped taxon node and its typed TaxonId property + organism annotation are in the payload.
        Assert.Contains("taxon:3702", html)
        Assert.Contains("Arabidopsis thaliana", html)

    [<Fact>]
    member _.``dangling reference targets are embedded as Missing placeholder nodes`` () =
        Assert.Contains("\"Missing\"", html)

    [<Fact>]
    member _.``a value containing markup cannot break out of the script block`` () =
        let evil = ArcObject.create "x" ArcObjectKind.Resource [] [ Iri.Create "Note", ArcValue.String "</script><b>hi" ] []
        let out = Html.toString (ArcIR.Empty |> ArcIR.addObject evil)
        Assert.DoesNotContain("</script><b>hi", out)
        Assert.Contains("\\u003c/script>", out)

type SampleReferenceTests() =

    [<Fact>]
    member _.``experiment resolves its sample reference to the BioSample node, not the SRA sample accession`` () =
        // DRX066772's SAMPLE_DESCRIPTOR references the SRA sample accession DRS039895, but the
        // BioSample node is keyed by its BioSample accession SAMD00064197; the descriptor's
        // EXTERNAL_ID[namespace=BioSample] carries that key, so the hasSample edge must resolve to
        // SAMD00064197 and never dangle to DRS039895.
        let experiment = Experiment.read (TestFiles.fixture "DRX066772.xml") |> Seq.exactlyOne
        let sample = BioSample.read (TestFiles.fixture "SAMD00064197.xml") |> Seq.exactlyOne
        let ir = INSDC.build [ INSDC.experiment experiment; INSDC.bioSample sample ]

        let sampleTargets =
            ir.Relations
            |> Seq.filter (fun r -> r.Subject.Value = "DRX066772" && r.Predicate = Vocabulary.Rel.hasSample)
            |> Seq.map (fun r -> r.Object.Value)
            |> Seq.toList

        Assert.Contains("SAMD00064197", sampleTargets)
        Assert.DoesNotContain("DRS039895", sampleTargets)

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
        let empty = Discovery.parse "run_accession\texperiment_accession\n"
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
