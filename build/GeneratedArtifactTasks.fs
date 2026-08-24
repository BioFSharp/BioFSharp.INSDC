module GeneratedArtifactTasks

open System
open System.IO
open System.Text

open BlackFox.Fake

open BasicTasks
open FragmentSelectorTasks
open StructuralOntologyTasks

let private normalizedRelativePath root path =
    Path.GetRelativePath(root, path).Replace('\\', '/')

let private compareDirectories expectedRoot actualRoot =
    let files root =
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        |> Array.map (fun path -> normalizedRelativePath root path, path)
        |> Map.ofArray

    let expected = files expectedRoot
    let actual = files actualRoot
    let allPaths =
        Set.union
            (expected |> Map.keys |> Set.ofSeq)
            (actual |> Map.keys |> Set.ofSeq)

    allPaths
    |> Seq.choose (fun relative ->
        match Map.tryFind relative expected, Map.tryFind relative actual with
        | None, Some _ -> Some(sprintf "unexpected generated file: %s" relative)
        | Some _, None -> Some(sprintf "missing generated file: %s" relative)
        | Some expectedPath, Some actualPath ->
            if File.ReadAllBytes(expectedPath) = File.ReadAllBytes(actualPath) then
                None
            else
                Some(sprintf "changed generated file: %s" relative)
        | None, None -> None)
    |> List.ofSeq

let private compareTextFile path (expectedContent: string) =
    let expected = UTF8Encoding(false).GetBytes(expectedContent)

    if File.Exists path && File.ReadAllBytes(path) = expected then
        []
    elif File.Exists path then
        [ sprintf "changed generated file: %s" path ]
    else
        [ sprintf "missing generated file: %s" path ]

/// Regenerates all three committed artifacts into memory/a temporary directory
/// and fails on any byte-level drift without modifying the working tree.
let verifyGeneratedArtifacts =
    BuildTask.create "VerifyGeneratedArtifacts" [ buildSolution; restoreTools ] {
        let tempBase = Path.GetFullPath(Path.GetTempPath())
        let tempRoot = Path.Combine(tempBase, "BioFSharp.INSDC-generated-" + Guid.NewGuid().ToString("N"))
        let resolvedTemp = Path.GetFullPath(tempRoot)

        if not (resolvedTemp.StartsWith(tempBase, StringComparison.OrdinalIgnoreCase)) then
            failwithf "Refusing to use unexpected generated-artifact temp path: %s" resolvedTemp

        Directory.CreateDirectory(resolvedTemp) |> ignore

        try
            let generatedTypes = Path.Combine(resolvedTemp, "Generated")
            generateInsdcTypesTo generatedTypes

            let drift =
                [ yield!
                      compareDirectories
                          "src/BioFSharp.FileFormats.INSDC/Generated"
                          generatedTypes
                  yield!
                      compareTextFile
                          "src/BioFSharp.FileFormats.INSDC/FragmentSelectors.cs"
                          (generatedFragmentSelectorsContent ())
                  yield!
                      compareTextFile
                          "src/BioFSharp.IO.INSDC/StructuralOntology.obo"
                          (generatedStructuralOntologyContent ()) ]

            if not (List.isEmpty drift) then
                let details = drift |> List.truncate 30 |> String.concat Environment.NewLine
                let suffix = if List.length drift > 30 then "\n(additional differences omitted)" else ""
                failwithf
                    "Committed generated artifacts are stale. Run regenerateInsdcTypes, generateFragmentSelectors, and generateStructuralOntology, then commit their output.\n%s%s"
                    details
                    suffix

            printfn "Generated artifact drift check passed (types, fragment selectors, structural ontology)."
        finally
            if Directory.Exists(resolvedTemp) then
                Directory.Delete(resolvedTemp, true)
    }
