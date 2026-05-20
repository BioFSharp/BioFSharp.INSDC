module BasicTasks

open BlackFox.Fake
open Fake.IO
open Fake.DotNet
open Fake.IO.Globbing.Operators

open ProjectInfo

let clean = BuildTask.create "Clean" [] {
    !! "src/**/bin"
    ++ "src/**/obj"
    ++ "tests/**/bin"
    ++ "tests/**/obj"
    ++ "pkg"
    |> Shell.cleanDirs
}

// Regenerate the C# type model in BioFSharp.FileFormats.INSDC from the committed XSDs in schemas/.
// On-demand only — generated code is committed and the default build does NOT depend on this target.
// Run after touching schemas/ (or after pinning a new dotnet-xscgen version in .config/dotnet-tools.json).
//
// Flag choices:
//   --separateFiles : one .cs per generated class (cleaner diffs than a single 15k-line file)
//   -n BioFSharp.FileFormats.INSDC : map the default XML namespace to the package's C# namespace
//   -0  (--nullable) : nullable adapter properties for optional elements w/o defaults (needed for roundtrips)
//   -i l : map xs:integer to System.Int64
//
// TODO: future polish — add --tns / --tnsf substitutions to clean up the cross-schema namespace surface
// and remove the local rename hack in schemas/ENA.embl.xsd (see schemas/README.md).
let regenerateInsdcTypes = BuildTask.create "regenerateInsdcTypes" [] {
    let schemasDir  = "src/BioFSharp.FileFormats.INSDC/schemas"
    let generatedDir = "src/BioFSharp.FileFormats.INSDC/Generated"
    Shell.cleanDir generatedDir
    let schemaArgs =
        !! (schemasDir + "/*.xsd")
        |> Seq.map (fun p -> sprintf "\"%s\"" p)
        |> String.concat " "
    let args =
        sprintf "--separateFiles -n BioFSharp.FileFormats.INSDC -0 -i l -o \"%s\" %s" generatedDir schemaArgs
    let result = DotNet.exec id "xscgen" args
    if not result.OK then
        failwithf "dotnet xscgen failed (exit %d): %A" result.ExitCode result.Errors
}


let setPrereleaseTag = BuildTask.create "SetPrereleaseTag" [] {
    printfn "Please enter pre-release package suffix"
    let suffix = System.Console.ReadLine()
    prereleaseSuffix <- suffix
    prereleaseTag <- (sprintf "%s-%s" release.NugetVersion suffix)
    isPrerelease <- true
}

/// builds the solution file (dotnet build solution.sln)
let buildSolution =
    BuildTask.create "BuildSolution" [ clean ] { 
        solutionFile 
        |> DotNet.build (fun p ->
            { p with MSBuildParams = { p.MSBuildParams with DisableInternalBinLog = true }}
            |> DotNet.Options.withCustomParams (Some "-tl")
        )
    }

