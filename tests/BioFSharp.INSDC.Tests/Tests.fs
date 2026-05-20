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
