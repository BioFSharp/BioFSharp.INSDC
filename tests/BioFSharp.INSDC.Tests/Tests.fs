namespace BioFSharp.INSDC.Tests

open Xunit

open BioFSharp.IO.INSDC

/// Smoke tests that the IO surface compiles and the type aliases line up.
/// Real fixture-based read/roundtrip tests land in step 4 of plans/implementation.md.
type IoSurfaceSmokeTests() =

    [<Fact>]
    let ``BioProject.BioProject aliases the generated INSDC BioProject type`` () =
        Assert.Equal(typeof<BioProject.BioProject>, typeof<BioFSharp.FileFormats.INSDC.BioProject>)

    [<Fact>]
    let ``Every entity module exposes a writeString function over its type alias`` () =
        // Constructing a default-initialised record and round-tripping it through
        // writeString is the cheapest sanity check that XmlSerializer can handle each entity.
        let project    = BioProject.BioProject()
        let study      = Study.Study()
        let sample     = BioSample.BioSample()
        let experiment = Experiment.Experiment()
        let run        = Run.Run()
        let analysis   = Analysis.Analysis()
        let submission = Submission.Submission()
        let receipt    = Receipt.Receipt()
        // None of these should throw; we don't assert on the XML body shape here.
        BioProject.writeString  project    |> ignore
        Study.writeString       study      |> ignore
        BioSample.writeString   sample     |> ignore
        Experiment.writeString  experiment |> ignore
        Run.writeString         run        |> ignore
        Analysis.writeString    analysis   |> ignore
        Submission.writeString  submission |> ignore
        Receipt.writeString     receipt    |> ignore
