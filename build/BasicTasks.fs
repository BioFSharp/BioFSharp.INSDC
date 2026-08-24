module BasicTasks

open System
open System.IO
open System.Text
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
//   --separateFiles      : one .cs per generated class (cleaner diffs than a single 15k-line file)
//   -n <ns>              : map the default XML namespace to the package's C# namespace
//   -0  (--nullable)     : nullable adapter properties for optional elements w/o defaults (needed for roundtrips)
//   -i l                 : map xs:integer to System.Int64
//   --tnsf <file>        : substitute generated type names per schemas/typename-substitutions.txt
//                           (strips the Type infix from top-level + child types; cleans Type* enum prefixes)
let private schemasDir = "src/BioFSharp.FileFormats.INSDC/schemas"
let private generatedDir = "src/BioFSharp.FileFormats.INSDC/Generated"
let private substitutionFile = schemasDir + "/typename-substitutions.txt"

let private schemaFiles () =
    Directory.GetFiles(schemasDir, "*.xsd")
    |> Array.map (fun path -> path.Replace('\\', '/'))
    |> Array.sortWith (fun left right -> String.CompareOrdinal(left, right))

let private canonicalGeneratorCommand () =
    let inputs = schemaFiles () |> String.concat " "
    sprintf
        "// xscgen --separateFiles -n BioFSharp.FileFormats.INSDC -0 -i l --tnsf %s -o %s %s"
        substitutionFile
        generatedDir
        inputs

let private normalizeGeneratedFiles (outputDir: string) =
    let canonicalCommand = canonicalGeneratorCommand ()
    let utf8NoBom = UTF8Encoding(false)

    for path in Directory.GetFiles(outputDir, "*.cs", SearchOption.AllDirectories) do
        let normalized =
            File.ReadAllLines(path)
            |> Array.map (fun line ->
                if line.StartsWith("// xscgen ", StringComparison.Ordinal) then
                    canonicalCommand
                else
                    line)
            |> String.concat "\n"

        File.WriteAllText(path, normalized + "\n", utf8NoBom)

/// Restores the repository-local tools pinned in `.config/dotnet-tools.json`.
/// Generator targets depend on this so they also work on a clean CI runner.
let restoreTools = BuildTask.create "RestoreTools" [] {
    let result = DotNet.exec id "tool" "restore"
    if not result.OK then
        failwithf "dotnet tool restore failed (exit %d): %A" result.ExitCode result.Errors
}

/// Runs the pinned xscgen tool into `outputDir` and normalizes generator
/// metadata and line endings so output is byte-reproducible across machines.
let generateInsdcTypesTo (outputDir: string) =
    Shell.cleanDir outputDir
    let schemaArgs =
        schemaFiles ()
        |> Seq.map (sprintf "\"%s\"")
        |> String.concat " "
    let args =
        sprintf "--separateFiles -n BioFSharp.FileFormats.INSDC -0 -i l --tnsf \"%s\" -o \"%s\" %s"
            substitutionFile outputDir schemaArgs
    let result = DotNet.exec id "xscgen" args
    if not result.OK then
        failwithf "dotnet xscgen failed (exit %d): %A" result.ExitCode result.Errors
    normalizeGeneratedFiles outputDir

let regenerateInsdcTypes = BuildTask.create "regenerateInsdcTypes" [ restoreTools ] {
    generateInsdcTypesTo generatedDir
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
