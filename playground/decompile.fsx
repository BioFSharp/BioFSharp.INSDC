// Playground: load the locally-built BioFSharp.INSDC assemblies (plus their NuGet dependencies) and
// decompile one of the test fixtures into structural-ontology terms.
//
// Build the libraries first so the referenced DLLs exist (Debug output):
//     dotnet build src/BioFSharp.IO.INSDC/BioFSharp.IO.INSDC.fsproj
//
// Then run from the repo root:
//     dotnet fsi playground/decompile.fsx
//
// A "Could not resolve assembly: BioFSharp.FileFormats.INSDC.XmlSerializers" line may print first —
// that is .NET's XmlSerializer probing for an optional pre-generated serializer and falling back to
// runtime generation. It is harmless and does not affect the output.

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
let fixture =
    Path.Combine(__SOURCE_DIRECTORY__, "..", "tests", "fixtures", "PRJDB5192.xml")

let project = BioProject.read fixture |> Seq.head

let decompiled = BioProject.decompile project

printfn "Decompiled %s -> %d structural-ontology terms:\n" (Path.GetFileName fixture) decompiled.Length

for d in decompiled do
    printfn "%-50s = %s" d.Term.Name d.Value
    printfn "    xpath %s" d.XPath
