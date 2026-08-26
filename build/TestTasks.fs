module TestTasks

open BlackFox.Fake
open Fake.DotNet

open ProjectInfo
open BasicTasks
open GeneratedArtifactTasks
open DependencyAuditTasks


let runTests = BuildTask.create "RunTests" [verifyGeneratedArtifacts; dependencyAudit] {
    testProjects
    |> Seq.iter (fun testProject ->
        testProject
        |> Fake.DotNet.DotNet.test (fun testParams ->
            { testParams with
                Collect = Some "XPlat Code Coverage"
                Logger = Some "console;verbosity=detailed"
                Configuration = DotNet.BuildConfiguration.fromString configuration
                NoBuild = true
                MSBuildParams = { testParams.MSBuildParams with DisableInternalBinLog = true }
            }
        )
    )
}
