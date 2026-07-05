// Smoke script for the full INSDC -> ArcIR mapping.
//
// The ArcIR types and the mapping live in the built library src/BioFSharp.INSDC.ArcIR, so this script
// just references the assemblies (run `dotnet build` first):
//   dotnet build
//   dotnet fsi playground/ir_mapping.fsx

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

// One record per readable entity -> per-entity converters -> assemble + wire cross-entity refs.
let ir =
    [ INSDC.bioProject (one BioProject.read "PRJDB5192.xml")
      INSDC.study (one Study.read "DRP003416.xml")
      INSDC.bioSample (one BioSample.read "SAMD00064197.xml")
      INSDC.experiment (one Experiment.read "DRX066772.xml")
      INSDC.run (one Run.read "DRR072834.xml")
      INSDC.analysis (one Analysis.read "ERZ496533.xml")
      INSDC.submission (one Submission.read "DRA005154.xml")
      INSDC.receipt (Receipt.read (fixture "receipt-sample.xml")) ]
    |> INSDC.build

printfn "Objects (%d), grouped by kind:" ir.Objects.Count

ir.Objects.Values
|> Seq.groupBy (fun o -> o.Kind)
|> Seq.iter (fun (kind, os) ->
    printfn "  %A (%d):" kind (Seq.length os)
    for o in os do
        printfn "    %-24s  props=%-2d annotations=%d" o.Id.Value o.Properties.Count o.Annotations.Length)

printfn "\nRelations (%d), grouped by predicate:" ir.Relations.Count

ir.Relations
|> Seq.groupBy (fun r -> r.Predicate.Value)
|> Seq.iter (fun (pred, rs) ->
    printfn "  %s (%d):" pred (Seq.length rs)
    for r in rs do
        printfn "    %s --> %s" r.Subject.Value r.Object.Value)
