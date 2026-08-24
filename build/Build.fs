open BlackFox.Fake
open System.IO
open Fake.Core
open Fake.DotNet
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open Fake.Tools

open Helpers

initializeContext()

open BasicTasks
open FragmentSelectorTasks
open StructuralOntologyTasks
open GeneratedArtifactTasks
open DependencyAuditTasks
open TestTasks
open PackageTasks
open ReleaseTasks
open ReleaseFromNotesTask

/// Full release of NuGet packages and the stable git tag.
let _release = 
    BuildTask.createEmpty 
        "Release" 
        [clean; buildSolution; runTests; pack; createTag; publishNuget]

/// Full release of NuGet packages and the prerelease git tag.
let _preRelease = 
    BuildTask.createEmpty 
        "PreRelease" 
        [setPrereleaseTag; clean; buildSolution; runTests; packPrerelease; createPrereleaseTag; publishNugetPrerelease]

/// Legacy alias for `Release`, retained after local docs publishing was removed.
let _releaseNoDocs = 
    BuildTask.createEmpty 
        "ReleaseNoDocs" 
        [clean; buildSolution; runTests; pack; createTag; publishNuget;]

/// Legacy alias for `PreRelease`, retained after local docs publishing was removed.
let _preReleaseNoDocs =
    BuildTask.createEmpty
        "PreReleaseNoDocs"
        [setPrereleaseTag; clean; buildSolution; runTests; packPrerelease; createPrereleaseTag; publishNugetPrerelease]

// Force ReleaseFromNotesTask to be initialized so its BuildTask.create call
// registers the target. Without a reference here F# would skip the module's
// top-level bindings (target wouldn't be discoverable from the CLI).
let _releaseFromNotes = releaseFromNotes

// Same forced-init reason as above: reference the target so its module's top-level
// BuildTask.create call runs and registers `generateFragmentSelectors` for the CLI.
let _generateFragmentSelectors = generateFragmentSelectors

// Same forced-init reason: register `generateStructuralOntology` for the CLI.
let _generateStructuralOntology = generateStructuralOntology

// Register the Phase 1 verification gates for direct CLI use.
let _verifyGeneratedArtifacts = verifyGeneratedArtifacts
let _dependencyAudit = dependencyAudit

[<EntryPoint>]
let main args = 
    runOrDefault buildSolution args
