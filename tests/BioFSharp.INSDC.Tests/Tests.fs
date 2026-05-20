namespace BioFSharp.INSDC.Tests

open Xunit

open BioFSharp.IO.INSDC

/// Smoke tests that the IO surface compiles and the type aliases line up.
/// Real fixture-based read/roundtrip tests land in step 4 of plans/implementation.md.
type IoSurfaceSmokeTests() =

    [<Fact>]
    let ``BioProject.Project aliases the generated INSDC Project type`` () =
        Assert.Equal(typeof<BioProject.Project>, typeof<BioFSharp.FileFormats.INSDC.Project>)

    [<Fact>]
    let ``Every entity module exposes a writeString function over its type alias`` () =
        // Constructing a default-initialised record and round-tripping it through
        // writeString is the cheapest sanity check that XmlSerializer can handle each entity.
        let project    = BioProject.Project()
        let study      = Study.Study()
        let sample     = Sample.Sample()
        let experiment = Experiment.Experiment()
        let run        = Run.Run()
        let analysis   = Analysis.Analysis()
        let submission = Submission.Submission()
        let receipt    = Receipt.Receipt()
        // None of these should throw; we don't assert on the XML body shape here.
        BioProject.writeString  project    |> ignore
        Study.writeString       study      |> ignore
        Sample.writeString      sample     |> ignore
        Experiment.writeString  experiment |> ignore
        Run.writeString         run        |> ignore
        Analysis.writeString    analysis   |> ignore
        Submission.writeString  submission |> ignore
        Receipt.writeString     receipt    |> ignore
