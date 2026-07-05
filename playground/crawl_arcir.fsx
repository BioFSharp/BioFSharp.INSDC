// Crawls a live project from ENA and writes interactive ArcIR graphs (self-contained HTML).
//
// Two views are written next to this script:
//   crawl_<acc>.html         — the full connected graph
//   crawl_<acc>_subset.html  — a minimal, readable subset: the project, its study, one
//                              sample, and the experiment + run that used that sample (+ FASTQ)
//
// Beyond the record-derived graph this adds each run's FASTQ files as Resource nodes: they
// are the runs' actual output but live only in the ENA filereport (fastq_ftp), NOT in the run
// XML — so it uses Crawler.crawlAndDiscover to get the records AND the per-run connectivity/files.
//
// The libraries are referenced from their build output, so run `dotnet build` first (this one
// hits the network, unlike the fixture-based scripts):
//   dotnet build
//   dotnet fsi playground/crawl_arcir.fsx

#r "nuget: BioFSharp, 2.0.0-preview.3"
#r "nuget: OBO.NET, 0.6.0"
#r "nuget: System.ComponentModel.Annotations, 5.0.0"
#r "nuget: FsHttp, 15.0.3"
#r "nuget: Microsoft.Data.Sqlite, 8.0.10"

#r "../src/BioFSharp.FileFormats.INSDC/bin/Release/netstandard2.0/BioFSharp.FileFormats.INSDC.dll"
#r "../src/BioFSharp.IO.INSDC/bin/Release/netstandard2.0/BioFSharp.IO.INSDC.dll"
#r "../src/BioFSharp.INSDC.SQLite/bin/Release/netstandard2.0/BioFSharp.INSDC.SQLite.dll"
#r "../src/BioFSharp.INSDC.ArcIR/bin/Release/netstandard2.0/BioFSharp.INSDC.ArcIR.dll"
#r "../src/BioFSharp.INSDC.Crawler/bin/Release/net8.0/BioFSharp.INSDC.Crawler.dll"

open System.IO
open Arc.Build
open BioFSharp.INSDC.ArcIR
open BioFSharp.INSDC.Crawler

let accession = "PRJDB5192"

// Live crawl: the typed records + the discovery rows (per-run connectivity + FASTQ files).
let records, discovered = Crawler.crawlAndDiscover accession

// Each FASTQ file (from the filereport) -> a Data resource node + a `producesData` edge from its
// run. Mirrors SubObjects.runFile, which only fires when the run XML carries files — here it does
// not, so the run's real output would otherwise be missing from the graph.
let fastqFragments (rows: DiscoveryRow list) : (ArcObject * ArcRelation) list =
    rows
    |> List.collect (fun row ->
        row.FastqFiles
        |> List.map (fun file ->
            let name = file.Url.Split('/') |> Array.last
            let id = row.RunAccession + "#fastq:" + name

            let props =
                [ Iri.Create "Filename", ArcValue.String name
                  Iri.Create "Url", ArcValue.String file.Url ]
                @ (if file.Bytes = "" then [] else [ (Iri.Create "Bytes", ArcValue.String file.Bytes) ])
                @ (if file.Md5 = "" then [] else [ (Iri.Create "Md5", ArcValue.String file.Md5) ])

            let node = ArcObject.create id ArcObjectKind.Resource [ Vocabulary.DType.data ] props []
            node, ArcRelation.create row.RunAccession Vocabulary.Rel.producesData id [] []))

// Add (node, parent-edge) fragments to an IR.
let withFragments (fragments: (ArcObject * ArcRelation) list) (ir: ArcIR) : ArcIR =
    ir
    |> ArcIR.addObjects (fragments |> List.map fst)
    |> ArcIR.addRelations (fragments |> List.map snd)

// Drop edges whose target is not a real node, so subsets stay clean (no dangling "Missing" nodes).
let pruneDangling (ir: ArcIR) : ArcIR =
    { ir with Relations = ir.Relations |> Set.filter (fun r -> ir.Objects.ContainsKey r.Object) }

// Build an IR from a set of records (each mapped through its INSDC converter) + FASTQ fragments.
let buildIr (bps, studies, samples, exps, runs) (rows: DiscoveryRow list) : ArcIR =
    [ yield! bps |> Array.map INSDC.bioProject
      yield! studies |> Array.map INSDC.study
      yield! samples |> Array.map INSDC.bioSample
      yield! exps |> Array.map INSDC.experiment
      yield! runs |> Array.map INSDC.run ]
    |> INSDC.build
    |> withFragments (fastqFragments rows)
    |> pruneDangling

let writeHtml suffix ir =
    let path = Path.Combine(__SOURCE_DIRECTORY__, sprintf "crawl_%s%s.html" accession suffix)
    Html.writeFile path ir
    path

// --- Full graph --------------------------------------------------------------
let fullIr =
    buildIr
        (records.BioProjects, records.Studies, records.BioSamples, records.Experiments, records.Runs)
        discovered.Rows

let fullPath = writeHtml "" fullIr

// --- Subset graph: project + study + one sample + the experiment/run that used it ------------
// The study is kept as the connector (project links to the rest only via its study).
let subsetPath =
    discovered.Rows
    |> List.tryFind (fun r -> r.SampleAccession <> "" && r.ExperimentAccession <> "" && r.RunAccession <> "")
    |> Option.map (fun row ->
        let subsetIr =
            buildIr
                (records.BioProjects |> Array.filter (fun x -> x.Accession = row.ProjectAccession),
                 records.Studies |> Array.filter (fun x -> x.Accession = row.StudyAccession),
                 records.BioSamples |> Array.filter (fun x -> x.Accession = row.SampleAccession),
                 records.Experiments |> Array.filter (fun x -> x.Accession = row.ExperimentAccession),
                 records.Runs |> Array.filter (fun x -> x.Accession = row.RunAccession))
                [ row ]

        writeHtml "_subset" subsetIr, row)

printfn "\nCrawled %s -> ArcIR graphs (open in a browser):" accession
printfn "  full:   %s  (%d nodes, %d edges)" fullPath fullIr.Objects.Count fullIr.Relations.Count

match subsetPath with
| Some(path, row) ->
    printfn
        "  subset: %s  (project %s / study %s / sample %s / experiment %s / run %s)"
        path
        row.ProjectAccession
        row.StudyAccession
        row.SampleAccession
        row.ExperimentAccession
        row.RunAccession
| None -> printfn "  subset: skipped (no connected run found)"
