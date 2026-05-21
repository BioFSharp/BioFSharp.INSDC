module ProjectInfo

open Fake.Core


let project = "BioFSharp.INSDC"

let testProjects = 
    [
        "tests/BioFSharp.INSDC.Tests/BioFSharp.INSDC.Tests.fsproj"
    ]

let solutionFile  = $"{project}.slnx"

let configuration = "Release"

let gitOwner = "BioFSharp"

let gitHome = $"https://github.com/{gitOwner}"

let projectRepo = $"https://github.com/{gitOwner}/{project}"

let pkgDir = "pkg"


// Create RELEASE_NOTES.md if not existing. Or "release" would throw an error.
Fake.Extensions.Release.ReleaseNotes.ensure()

let release = ReleaseNotes.load "RELEASE_NOTES.md"

let stableVersion = SemVer.parse release.NugetVersion

// Use the full NugetVersion from RELEASE_NOTES.md so prerelease suffixes
// (e.g. "0.0.0-preview.1") survive into the nupkg version and the git tag.
let stableVersionTag = release.NugetVersion

let mutable prereleaseSuffix = ""

let mutable prereleaseTag = ""

let mutable isPrerelease = false

