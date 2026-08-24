module DependencyAuditTasks

open System
open System.Diagnostics
open System.IO
open System.Text.Json

open BlackFox.Fake

open BasicTasks
open ProjectInfo

type private Vulnerability =
    { Project: string
      Package: string
      Version: string
      Severity: string
      AdvisoryUrl: string }

type private Suppression =
    { Project: string
      Package: string
      AdvisoryUrl: string
      Expires: DateTime
      Reason: string }

let private tryProperty (name: string) (element: JsonElement) =
    let mutable value = Unchecked.defaultof<JsonElement>
    if element.TryGetProperty(name, &value) then Some value else None

let private requiredString name element =
    match tryProperty name element with
    | Some value when value.ValueKind = JsonValueKind.String -> value.GetString()
    | _ -> failwithf "Dependency-audit suppression is missing string property '%s'." name

let private repositoryPath path =
    let absolute = Path.GetFullPath(path)
    Path.GetRelativePath(Environment.CurrentDirectory, absolute).Replace('\\', '/')

let private readSuppressions () =
    let path = "build/dependency-audit-suppressions.json"
    use document = JsonDocument.Parse(File.ReadAllText(path))

    document.RootElement.GetProperty("suppressions").EnumerateArray()
    |> Seq.map (fun item ->
        let expiresText = requiredString "expires" item
        let mutable expires = DateTime.MinValue

        if not (DateTime.TryParseExact(expiresText, "yyyy-MM-dd", null, Globalization.DateTimeStyles.None, &expires)) then
            failwithf "Dependency-audit suppression expiry '%s' must use yyyy-MM-dd." expiresText

        let suppression =
            { Project = requiredString "project" item
              Package = requiredString "package" item
              AdvisoryUrl = requiredString "advisoryUrl" item
              Expires = expires
              Reason = requiredString "reason" item }

        if String.IsNullOrWhiteSpace suppression.Reason then
            failwith "Dependency-audit suppressions require a non-empty review reason."

        suppression)
    |> List.ofSeq

let private runAudit () =
    let startInfo = ProcessStartInfo()
    startInfo.FileName <- "dotnet"
    startInfo.UseShellExecute <- false
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true

    [ "list"
      solutionFile
      "package"
      "--vulnerable"
      "--include-transitive"
      "--format"
      "json"
      "--no-restore" ]
    |> List.iter startInfo.ArgumentList.Add

    use auditProcess = Process.Start(startInfo)
    let stdout = auditProcess.StandardOutput.ReadToEndAsync()
    let stderr = auditProcess.StandardError.ReadToEndAsync()
    auditProcess.WaitForExit()
    let output = stdout.Result
    let errors = stderr.Result

    if auditProcess.ExitCode <> 0 then
        failwithf "dotnet dependency audit failed (exit %d):\n%s\n%s" auditProcess.ExitCode output errors

    use document = JsonDocument.Parse(output)

    [ for project in document.RootElement.GetProperty("projects").EnumerateArray() do
          let projectPath = project.GetProperty("path").GetString() |> repositoryPath

          match tryProperty "frameworks" project with
          | None -> ()
          | Some frameworks ->
              for framework in frameworks.EnumerateArray() do
                  for packageGroup in [ "topLevelPackages"; "transitivePackages" ] do
                      match tryProperty packageGroup framework with
                      | None -> ()
                      | Some packages ->
                          for package in packages.EnumerateArray() do
                              match tryProperty "vulnerabilities" package with
                              | None -> ()
                              | Some vulnerabilities ->
                                  for vulnerability in vulnerabilities.EnumerateArray() do
                                      yield
                                          { Project = projectPath
                                            Package = package.GetProperty("id").GetString()
                                            Version = package.GetProperty("resolvedVersion").GetString()
                                            Severity = vulnerability.GetProperty("severity").GetString()
                                            AdvisoryUrl = vulnerability.GetProperty("advisoryurl").GetString() } ]

let private matches (suppression: Suppression) (vulnerability: Vulnerability) =
    String.Equals(suppression.Project, vulnerability.Project, StringComparison.OrdinalIgnoreCase)
    && String.Equals(suppression.Package, vulnerability.Package, StringComparison.OrdinalIgnoreCase)
    && String.Equals(suppression.AdvisoryUrl, vulnerability.AdvisoryUrl, StringComparison.OrdinalIgnoreCase)

/// Audits direct and transitive NuGet packages. Every reported advisory fails
/// the build unless an exact project/package/advisory suppression with a review
/// reason and unexpired date exists; stale or unused suppressions also fail.
let dependencyAudit =
    BuildTask.create "DependencyAudit" [ buildSolution ] {
        let today = DateTime.UtcNow.Date
        let suppressions = readSuppressions ()
        let expired = suppressions |> List.filter (fun item -> item.Expires < today)

        if not (List.isEmpty expired) then
            expired
            |> List.map (fun item -> sprintf "%s / %s / %s" item.Project item.Package item.AdvisoryUrl)
            |> String.concat "\n"
            |> failwithf "Dependency-audit suppressions have expired:\n%s"

        let vulnerabilities = runAudit ()
        let unsuppressed = vulnerabilities |> List.filter (fun item -> suppressions |> List.exists (fun s -> matches s item) |> not)
        let unused = suppressions |> List.filter (fun item -> vulnerabilities |> List.exists (matches item) |> not)

        if not (List.isEmpty unused) then
            unused
            |> List.map (fun item -> sprintf "%s / %s / %s" item.Project item.Package item.AdvisoryUrl)
            |> String.concat "\n"
            |> failwithf "Dependency-audit suppressions no longer match an advisory; remove them:\n%s"

        for vulnerability in vulnerabilities do
            match suppressions |> List.tryFind (fun suppression -> matches suppression vulnerability) with
            | Some suppression ->
                printfn
                    "SUPPRESSED %s %s %s (%s; expires %s): %s"
                    vulnerability.Package
                    vulnerability.Version
                    vulnerability.AdvisoryUrl
                    vulnerability.Project
                    (suppression.Expires.ToString("yyyy-MM-dd"))
                    suppression.Reason
            | None -> ()

        if not (List.isEmpty unsuppressed) then
            unsuppressed
            |> List.map (fun item ->
                sprintf
                    "%s %s [%s] in %s — %s"
                    item.Package
                    item.Version
                    item.Severity
                    item.Project
                    item.AdvisoryUrl)
            |> String.concat "\n"
            |> failwithf "Unsuppressed vulnerable dependencies:\n%s"

        printfn "Dependency audit passed (%d reviewed suppression(s))." suppressions.Length
    }
