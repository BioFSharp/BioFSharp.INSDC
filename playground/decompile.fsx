// Playground: load the locally-built BioFSharp.INSDC assemblies (plus their NuGet dependencies) and
// decompile one of the test fixtures into structural-ontology terms.
//
// Build the libraries first so the referenced DLLs exist (Debug output):
//     dotnet build src/BioFSharp.IO.INSDC/BioFSharp.IO.INSDC.fsproj
//
// Then run from the repo root:
//     dotnet fsi playground/decompile.fsx

// NuGet dependencies of the project assemblies. `#r "nuget:"` restores them; referencing a local DLL
// does not pull in its package deps, so they are listed explicitly. Versions match the .fsproj/.csproj.
#r "nuget: BioFSharp, 2.0.0-preview.3"
#r "nuget: OBO.NET, 0.6.0"
#r "nuget: System.ComponentModel.Annotations, 5.0.0"

// Locally-built project assemblies. Paths are relative to this script's directory (playground/).
#r "../src/BioFSharp.FileFormats.INSDC/bin/Debug/netstandard2.0/BioFSharp.FileFormats.INSDC.dll"
#r "../src/BioFSharp.IO.INSDC/bin/Debug/netstandard2.0/BioFSharp.IO.INSDC.dll"

open System.IO

open BioFSharp.IO.INSDC

// __SOURCE_DIRECTORY__ is playground/, so the fixtures live one level up under tests/fixtures.
let bioproject_fixture =
    Path.Combine(__SOURCE_DIRECTORY__, "..", "tests", "fixtures", "PRJDB5192.xml")

let biosample_fixture =
    Path.Combine(__SOURCE_DIRECTORY__, "..", "tests", "fixtures", "SAMD00064197.xml")

let experiment_fixture =
    Path.Combine(__SOURCE_DIRECTORY__, "..", "tests", "fixtures", "DRX066772.xml")

let project = BioProject.read bioproject_fixture |> Seq.head

let decompiled_project = BioProject.decompile project

for d in decompiled_project do
    printfn "%s\t%-20s\t%s" (d.Term.Name.PadRight(90)) (((d.Value.Substring(0, min 17 d.Value.Length)) + "...").PadRight(20)) d.XPath

let sample = BioSample.read biosample_fixture |> Seq.head

let decompiled_sample = BioSample.decompile sample

for d in decompiled_sample do
    printfn "%s\t%-20s\t%s" (d.Term.Name.PadRight(90)) (((d.Value.Substring(0, min 17 d.Value.Length)) + "...").PadRight(20)) d.XPath

let experiment = Experiment.read experiment_fixture |> Seq.head

let decompiled_experiment = Experiment.decompile experiment

for d in decompiled_experiment do
    printfn "%s\t%-20s\t%s" (d.Term.Name.PadRight(90)) (((d.Value.Substring(0, min 17 d.Value.Length)) + "...").PadRight(20)) d.XPath