module ReleaseFromNotesTask

open BlackFox.Fake
open Fake.Core
open Fake.DotNet
open Fake.IO
open Fake.IO.Globbing.Operators
open Fake.Tools

open ProjectInfo

open System.Text.RegularExpressions

// Owns *all* logic for the "release on RELEASE_NOTES.md change" CI flow:
// parse the topmost ### header, decide whether to skip (Unreleased / tag
// already exists), and otherwise run the full release pipeline (clean →
// build → test → pack → push → tag). The GitHub Actions workflow stays
// thin and only invokes this target.
//
// Why duplicate a few lines from PackageTasks / ReleaseTasks instead of
// chaining their build-tasks? Those targets gate on interactive prompts
// (auto-accepted via CI=true) and unconditionally chain clean + build +
// test, which we want to skip entirely when the gate says no release.
// Keeping a self-contained body lets the gate short-circuit cleanly.

/// First "### <version>" line from RELEASE_NOTES.md (raw, so we can detect
/// the (Unreleased) marker — Fake.Core.ReleaseNotes.load strips it).
let private firstHeaderLine () =
    System.IO.File.ReadLines "RELEASE_NOTES.md"
    |> Seq.tryFind (fun line -> line.StartsWith "### ")

let private tagExists (tag: string) =
    // `git rev-parse --verify --quiet refs/tags/<tag>` exits 0 iff the ref resolves.
    let ok, _, _ =
        Git.CommandHelper.runGitCommand "" (sprintf "rev-parse --verify --quiet refs/tags/%s" tag)
    ok

let private cleanArtifacts () =
    !! "src/**/bin"
    ++ "src/**/obj"
    ++ "tests/**/bin"
    ++ "tests/**/obj"
    ++ pkgDir
    |> Shell.cleanDirs

let private buildAndTest () =
    solutionFile
    |> DotNet.build (fun p ->
        { p with
            Configuration = DotNet.BuildConfiguration.fromString configuration
            MSBuildParams = { p.MSBuildParams with DisableInternalBinLog = true } }
        |> DotNet.Options.withCustomParams (Some "-tl"))

    for testProject in testProjects do
        testProject
        |> DotNet.test (fun p ->
            { p with
                Configuration = DotNet.BuildConfiguration.fromString configuration
                NoBuild = true
                MSBuildParams = { p.MSBuildParams with DisableInternalBinLog = true } }
            |> DotNet.Options.withCustomParams (Some "-tl"))

// Mirrors PackageTasks.replaceCommitLink — keep PackageReleaseNotes free of
// the `[[#sha](url)]` link prefixes some release notes carry.
let private commitLinkPattern = Regex @"\[\[#[a-z0-9]*\]\(.*\)\] "

let private packAll (version: string) =
    let notes =
        release.Notes
        |> List.map (fun line -> commitLinkPattern.Replace(line, ""))
        |> String.concat "\r\n"

    !! "src/**/*.*proj"
    -- "src/bin/*"
    |> Seq.iter (DotNet.pack (fun p ->
        let msb =
            { p.MSBuildParams with
                Properties =
                    [ "Version", version
                      "PackageReleaseNotes", notes ]
                    @ p.MSBuildParams.Properties
                DisableInternalBinLog = true }
        { p with
            Configuration = DotNet.BuildConfiguration.fromString configuration
            MSBuildParams = msb
            OutputPath = Some pkgDir
            NoBuild = true }
        |> DotNet.Options.withCustomParams (Some "-tl")))

let private pushNupkgs () =
    let apikey =
        match Environment.environVarOrNone "NUGET_API_KEY" with
        | Some k when k <> "" -> k
        | _ -> failwith "NUGET_API_KEY env var is not set."
    let source = "https://api.nuget.org/v3/index.json"
    let artifacts = !! (sprintf "%s/*.*pkg" pkgDir) |> Seq.toList
    if List.isEmpty artifacts then
        failwithf "No packages found under %s/ to push." pkgDir
    for artifact in artifacts do
        let result = DotNet.exec id "nuget" (sprintf "push -s %s -k %s %s --skip-duplicate" source apikey artifact)
        if not result.OK then
            failwithf "dotnet nuget push failed for %s" artifact

let private pushReleaseTag (version: string) =
    // GitHub Actions checkout doesn't preconfigure committer identity; set a
    // bot identity here so the annotated tag has author metadata. No FAKE
    // wrapper for `git config`, so use runSimpleGitCommand (throws on failure).
    Git.CommandHelper.runSimpleGitCommand "" "config user.name 'github-actions[bot]'" |> ignore
    Git.CommandHelper.runSimpleGitCommand "" "config user.email 'github-actions[bot]@users.noreply.github.com'" |> ignore
    // `Git.Branches.tag` creates a *lightweight* tag — we want an annotated
    // one so the release carries author + message metadata. Drop to the raw
    // git command for that, then use Git.Branches.pushTag for the push.
    Git.CommandHelper.runSimpleGitCommand "" (sprintf "tag -a %s -m \"Release %s\"" version version) |> ignore
    Git.Branches.pushTag "" "origin" version

let releaseFromNotes = BuildTask.create "ReleaseFromNotes" [] {
    match firstHeaderLine () with
    | None ->
        failwith "RELEASE_NOTES.md has no '### <version>' header — cannot determine version to release."
    | Some header ->
        let version = stableVersionTag
        Trace.log (sprintf "Top RELEASE_NOTES.md entry: %s" header)
        Trace.log (sprintf "Parsed version: %s" version)
        if Regex.IsMatch(header, @"\(unreleased\)", RegexOptions.IgnoreCase) then
            Trace.log (sprintf "Entry is marked (Unreleased) — skipping release of %s." version)
        elif tagExists version then
            Trace.log (sprintf "Git tag %s already exists — skipping release." version)
        else
            Trace.log (sprintf "Releasing %s..." version)
            cleanArtifacts ()
            buildAndTest ()
            packAll version
            pushNupkgs ()
            pushReleaseTag version
            Trace.log (sprintf "Released %s." version)
}
