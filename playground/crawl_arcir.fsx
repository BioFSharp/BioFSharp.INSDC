// Crawls a live BioProject from ENA and writes its canonical ArcIR JSON state.
//
// The crawler follows ENA's read-run report and therefore retrieves every connected
// BioProject, Study, BioSample, Experiment, and Run record. FASTQ resources reported
// by the same discovery response are included as Run outputs even though they are not
// present in the Run XML. The reviewed base SSSOM mapping is then applied additively.
//
// Usage (arguments are optional):
//   dotnet build -c Release
//   dotnet fsi playground/crawl_arcir.fsx -- [accession] [output.arcir.json] [mapping.sssom.tsv]
//
// Defaults:
//   accession: PRJDB5192
//   output:    playground/crawl_<accession>.arcir.json
//   mapping:   tests/fixtures/sssom/INSDCER-ARC.sssom.tsv

#r "nuget: BioFSharp, 2.0.0-preview.3"
#r "nuget: OBO.NET, 0.6.0"
#r "nuget: PolyglotSSSOM, 0.1.0-alpha.1"
#r "nuget: System.ComponentModel.Annotations, 5.0.0"
#r "nuget: FsHttp, 15.0.3"
#r "nuget: Microsoft.Data.Sqlite, 8.0.30"

#r "../src/BioFSharp.ArcIR/bin/Release/netstandard2.0/BioFSharp.ArcIR.dll"
#r "../src/BioFSharp.FileFormats.INSDC/bin/Release/netstandard2.0/BioFSharp.FileFormats.INSDC.dll"
#r "../src/BioFSharp.IO.INSDC/bin/Release/netstandard2.0/BioFSharp.IO.INSDC.dll"
#r "../src/BioFSharp.INSDC.SQLite/bin/Release/netstandard2.0/BioFSharp.INSDC.SQLite.dll"
#r "../src/BioFSharp.INSDC.ArcIR/bin/Release/netstandard2.0/BioFSharp.INSDC.ArcIR.dll"
#r "../src/BioFSharp.INSDC.Crawler/bin/Release/net8.0/BioFSharp.INSDC.Crawler.dll"

open System
open System.Globalization
open System.IO
open BioFSharp.ArcIR
open BioFSharp.INSDC.ArcIR
open BioFSharp.INSDC.Crawler

let arguments = fsi.CommandLineArgs |> Array.skip 1
let accession = arguments |> Array.tryItem 0 |> Option.defaultValue "PRJDB5192"

let outputPath =
    arguments
    |> Array.tryItem 1
    |> Option.defaultValue (Path.Combine(__SOURCE_DIRECTORY__, $"crawl_{accession}.arcir.json"))
    |> Path.GetFullPath

let mappingPath =
    arguments
    |> Array.tryItem 2
    |> Option.defaultValue (
        Path.Combine(__SOURCE_DIRECTORY__, "..", "tests", "fixtures", "sssom", "INSDCER-ARC.sssom.tsv")
    )
    |> Path.GetFullPath

let formatPersistenceErrors (errors: PersistenceError list) =
    errors
    |> List.map (fun error -> $"{error.Code}: {error.Message}")
    |> String.concat Environment.NewLine

let expectPersisted operation (result: Result<'value, PersistenceError list>) =
    match result with
    | Ok value -> value
    | Error errors -> failwith $"{operation} failed:{Environment.NewLine}{formatPersistenceErrors errors}"

let nonBlank value = not (String.IsNullOrWhiteSpace value)

// The Run XML often omits files that ENA exposes through the discovery report.
// Materialize those files as ordinary ArcIR Resource objects and producesData edges.
let fastqFragments (rows: DiscoveryRow list) : (ArcObject * ArcRelation) list =
    rows
    |> List.collect (fun row ->
        row.FastqFiles
        |> List.map (fun file ->
            let name = file.Url.Split('/') |> Array.last
            let rawId = row.RunAccession + "#fastq:" + name

            let bytes =
                match Int64.TryParse(file.Bytes, NumberStyles.None, CultureInfo.InvariantCulture) with
                | true, value -> Some(Vocabulary.Property.ofName "Bytes", ArcValue.Integer value)
                | _ -> None

            let properties =
                [ if nonBlank name then
                      Vocabulary.Property.ofName "Filename", ArcValue.String name
                  if nonBlank file.Url then
                      Vocabulary.Property.ofName "Url", ArcValue.String file.Url
                  if nonBlank file.Md5 then
                      Vocabulary.Property.ofName "Md5", ArcValue.String file.Md5
                  yield! bytes |> Option.toList ]

            GraphBuilder.object' rawId ArcObjectKind.Resource [ Vocabulary.DType.data ] properties [],
            GraphBuilder.relation row.RunAccession Vocabulary.Rel.producesData rawId [] []))

let isRelatedProjectRelation predicate =
    predicate = Vocabulary.Rel.hasParentProject
    || predicate = Vocabulary.Rel.hasChildProject
    || predicate = Vocabulary.Rel.hasPeerProject

// A project record can designate a related project outside its own run-connected
// crawl. Keep that designation as a typed, reference-only node; an empty annotation
// set makes clear that the related record itself was not fetched.
let materializeRelatedProjectTargets (graph: ArcIR) =
    let referenceOnlyProjects =
        graph.Relations.Values
        |> Seq.filter (fun relation ->
            isRelatedProjectRelation relation.Predicate
            && not (graph.Objects.ContainsKey relation.Object))
        |> Seq.map _.Object
        |> Seq.distinct
        |> Seq.map (fun id ->
            GraphBuilder.object'
                id.Value
                ArcObjectKind.Collection
                [ Vocabulary.DType.bioProject; Vocabulary.DType.investigation ]
                []
                [])
        |> List.ofSeq

    let completed =
        GraphBuilder.assemble
            (Seq.append graph.Objects.Values referenceOnlyProjects)
            graph.Relations.Values

    completed, referenceOnlyProjects.Length

let buildGraph (records: CrawlResult) (discovered: DiscoveredSet) =
    let recordGraph =
        [ yield! records.BioProjects |> Seq.map INSDC.bioProject
          yield! records.Studies |> Seq.map INSDC.study
          yield! records.BioSamples |> Seq.map INSDC.bioSample
          yield! records.Experiments |> Seq.map INSDC.experiment
          yield! records.Runs |> Seq.map INSDC.run ]
        |> INSDC.build

    let fastq = fastqFragments discovered.Rows

    GraphBuilder.assemble
        (Seq.append recordGraph.Objects.Values (fastq |> Seq.map fst))
        (Seq.append recordGraph.Relations.Values (fastq |> Seq.map snd))
    |> materializeRelatedProjectTargets

let applyBaseMapping graph =
    let loaded = mappingPath |> File.ReadAllText |> SssomMapping.loadEmbedded

    let mappingErrors =
        loaded.Diagnostics
        |> List.filter (fun diagnostic -> diagnostic.Severity = DiagnosticSeverity.Error)

    if not mappingErrors.IsEmpty then
        let details = mappingErrors |> List.map _.Message |> String.concat Environment.NewLine
        failwith $"The base SSSOM mapping is invalid:{Environment.NewLine}{details}"

    match SemanticMapping.applyClaims (SssomMapping.exactMatches loaded) graph with
    | Ok result -> result
    | Error conflicts -> failwithf "The base mapping conflicts with the source graph: %A" conflicts

printfn "Crawling %s from ENA ..." accession
let records, discovered = Crawler.crawlAndDiscover accession
let sourceGraph, referenceOnlyProjectCount = buildGraph records discovered
let mapped = applyBaseMapping sourceGraph
let graph = mapped.Graph

let validationIssues = Validation.validate graph

if not validationIssues.IsEmpty then
    failwithf "The assembled ArcIR graph is invalid and was not written: %A" validationIssues

let revision = ArcIRJson.writeNew outputPath graph |> expectPersisted "ArcIR JSON write"

// Keep the low-effort viewer useful while the real frontend is developed.
let htmlPath = Path.ChangeExtension(outputPath, ".html")
Html.writeFile htmlPath graph

let recordCount =
    records.BioProjects.Length
    + records.Studies.Length
    + records.BioSamples.Length
    + records.Experiments.Length
    + records.Runs.Length

printfn "Wrote a complete project ArcIR state:"
printfn "  records:              %d" recordCount
printfn "  related project refs: %d" referenceOnlyProjectCount
printfn "  FASTQ resources:      %d" (discovered.Rows |> List.sumBy (fun row -> row.FastqFiles.Length))
printfn "  objects / relations:  %d / %d" graph.Objects.Count graph.Relations.Count
printfn "  mapping applications: %d" mapped.Applications.Length
printfn "  JSON:                  %s" revision.Path
printfn "  SHA-256:               %s" revision.Sha256
printfn "  HTML:                  %s" htmlPath
