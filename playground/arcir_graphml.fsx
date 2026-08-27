// Writes the full INSDC -> ArcIR graph after applying the reviewed base SSSOM mappings.
// The self-contained HTML view is the quickest way to inspect it: click the BioProject or Study node
// and compare the source INSDCER annotations with their additive ARC companion annotations.
//
// The ArcIR types, the mapping, and the GraphML serializer all live in the built library
// src/BioFSharp.INSDC.ArcIR, so this script just references the assemblies (run `dotnet build` first):
//   dotnet build
//   dotnet fsi playground/arcir_graphml.fsx
// Then in Gephi: File > Open (import as directed) > ForceAtlas 2 > Appearance > Nodes > Partition > kind.

#r "nuget: BioFSharp, 2.0.0-preview.3"
#r "nuget: OBO.NET, 0.6.0"
#r "nuget: PolyglotSSSOM, 0.1.0-alpha.1"
#r "nuget: System.ComponentModel.Annotations, 5.0.0"

#r "../src/BioFSharp.ArcIR/bin/Release/netstandard2.0/BioFSharp.ArcIR.dll"
#r "../src/BioFSharp.FileFormats.INSDC/bin/Release/netstandard2.0/BioFSharp.FileFormats.INSDC.dll"
#r "../src/BioFSharp.IO.INSDC/bin/Release/netstandard2.0/BioFSharp.IO.INSDC.dll"
#r "../src/BioFSharp.INSDC.ArcIR/bin/Release/netstandard2.0/BioFSharp.INSDC.ArcIR.dll"

open System.IO
open BioFSharp.ArcIR
open BioFSharp.IO.INSDC
open BioFSharp.INSDC.ArcIR

let fixture name = Path.Combine(__SOURCE_DIRECTORY__, "..", "tests", "fixtures", name)
let one reader file = reader (fixture file) |> Seq.head

// One record per readable entity -> per-entity converters -> assemble + wire cross-entity refs.
let sourceIr =
    [ INSDC.bioProject (one BioProject.read "PRJDB5192.xml")
      INSDC.study (one Study.read "DRP003416.xml")
      INSDC.bioSample (one BioSample.read "SAMD00064197.xml")
      INSDC.experiment (one Experiment.read "DRX066772.xml")
      INSDC.run (one Run.read "DRR072834.xml")
      INSDC.analysis (one Analysis.read "ERZ496533.xml")
      INSDC.submission (one Submission.read "DRA005154.xml")
      INSDC.receipt (Receipt.read (fixture "receipt-sample.xml")) ]
    |> INSDC.build

// Parse the reviewed external mapping artifact through PolyglotSSSOM, select the exact matches, and
// add mapped assertions beside their source assertions. The source graph remains unchanged in-place.
let mapping =
    fixture "sssom/INSDCER-ARC.sssom.tsv"
    |> File.ReadAllText
    |> SssomMapping.loadEmbedded

let mappingErrors =
    mapping.Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Severity = DiagnosticSeverity.Error)

if not mappingErrors.IsEmpty then
    failwithf "The base SSSOM mapping is invalid: %A" mappingErrors

let mapped =
    match SemanticMapping.applyClaims (SssomMapping.exactMatches mapping) sourceIr with
    | Ok result -> result
    | Error conflicts -> failwithf "The base mapping conflicted with the source graph: %A" conflicts

let ir = mapped.Graph

// GraphML for desktop tools (Gephi / yEd / Cytoscape).
let graphml = Path.Combine(__SOURCE_DIRECTORY__, "arcir.graphml")
GraphMl.writeFile graphml ir

// Self-contained interactive page: open in a browser, click a node to inspect its props + annotations.
let html = Path.Combine(__SOURCE_DIRECTORY__, "arcir.html")
Html.writeFile html ir

printfn "Wrote %d nodes and %d edges:" ir.Objects.Count ir.Relations.Count
printfn "  %d additive mapping applications" mapped.Applications.Length
printfn "  %s   (Gephi / yEd / Cytoscape)" graphml
printfn "  %s      (open in a browser)" html
