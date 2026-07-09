// Builds the INSDC -> ArcIR graph from the committed fixtures, then folds in two supplementary sources
// (see plans/arcir-ingest.md) and writes an interactive HTML graph to eyeball the enrichment:
//   - a paper (JATS XML)      -> a Publication resource + author Agents + a `references` edge to the project
//   - a count matrix (zipped) -> a CountMatrix resource + one CountColumn fragment per run column
//
// DRR072835 is a count column with no matching run record, so its `producesData` edge dangles and shows
// as a `Missing` placeholder node — the "including dangling relations" behaviour, live.
//
// Fixture-based (offline). Build the library first, then run:
//   dotnet build
//   dotnet fsi playground/ingest.fsx

#r "nuget: BioFSharp, 2.0.0-preview.3"
#r "nuget: OBO.NET, 0.6.0"
#r "nuget: System.ComponentModel.Annotations, 5.0.0"

#r "../src/BioFSharp.FileFormats.INSDC/bin/Release/netstandard2.0/BioFSharp.FileFormats.INSDC.dll"
#r "../src/BioFSharp.IO.INSDC/bin/Release/netstandard2.0/BioFSharp.IO.INSDC.dll"
#r "../src/BioFSharp.INSDC.ArcIR/bin/Release/netstandard2.0/BioFSharp.INSDC.ArcIR.dll"

open System.IO
open Arc.Build
open BioFSharp.IO.INSDC
open BioFSharp.INSDC.ArcIR

let fixture name = Path.Combine(__SOURCE_DIRECTORY__, "..", "tests", "fixtures", name)
let one reader file = reader (fixture file) |> Seq.head

// 1. The INSDC-derived graph (per-entity converters -> assemble + wire cross-entity refs).
let insdcIr =
    [ INSDC.bioProject (one BioProject.read "PRJDB5192.xml")
      INSDC.study (one Study.read "DRP003416.xml")
      INSDC.bioSample (one BioSample.read "SAMD00064197.xml")
      INSDC.experiment (one Experiment.read "DRX066772.xml")
      INSDC.run (one Run.read "DRR072834.xml") ]
    |> INSDC.build

// 2. Ingest supplementary sources as ConversionResult fragments keyed by accession.
let paper = Ingest.paperFromJats (fixture "paper-PRJDB5192.jats.xml") [ "PRJDB5192" ]
let counts = Ingest.countDataFromArchive (fixture "counts-PRJDB5192.zip")

// 3. Fold them into the INSDC graph (pending references resolve against the union; misses dangle).
let ir = Ingest.incorporate insdcIr (paper :: counts)

let html = Path.Combine(__SOURCE_DIRECTORY__, "ingest.html")
Html.writeFile html ir

let dangling =
    ir.Relations
    |> Seq.filter (fun r -> not (ir.Objects.ContainsKey r.Subject) || not (ir.Objects.ContainsKey r.Object))
    |> Seq.length

printfn "INSDC graph: %d nodes, %d edges" insdcIr.Objects.Count insdcIr.Relations.Count
printfn "+ paper + count data: %d nodes, %d edges (%d dangling)" ir.Objects.Count ir.Relations.Count dangling
printfn "  %s   (open in a browser)" html
