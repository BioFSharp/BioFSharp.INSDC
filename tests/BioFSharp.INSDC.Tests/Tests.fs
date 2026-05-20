namespace BioFSharp.INSDC.Tests

open Xunit

open BioFSharp.FileFormats.INSDC
open BioFSharp.IO.INSDC

/// Smoke tests that the IO surface compiles and `writeString` runs for every
/// entity. Real fixture-based read/roundtrip tests land in step 4 of
/// plans/implementation.md.
type IoSurfaceSmokeTests() =

    [<Fact>]
    let ``Every entity module's writeString runs on a default-initialised value`` () =
        // Constructing a default-initialised record and round-tripping it through
        // writeString is the cheapest sanity check that XmlSerializer can handle
        // each entity. We don't assert on the XML body shape here.
        BioProject.writeString  (BioProject())  |> ignore
        Study.writeString       (Study())       |> ignore
        BioSample.writeString   (BioSample())   |> ignore
        Experiment.writeString  (Experiment())  |> ignore
        Run.writeString         (Run())         |> ignore
        Analysis.writeString    (Analysis())    |> ignore
        Submission.writeString  (Submission())  |> ignore
        Receipt.writeString     (Receipt())     |> ignore
