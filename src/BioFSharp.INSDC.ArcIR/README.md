# BioFSharp.INSDC.ArcIR

Maps [INSDC](https://www.insdc.org/) (International Nucleotide Sequence Database
Collaboration) records into **ArcIR**, an ARC-oriented intermediate representation, as
part of the [`BioFSharp.INSDC`](https://github.com/BioFSharp/BioFSharp.INSDC) suite.

ArcIR is a property graph: parsed records become typed, related objects — projects,
studies, samples, experiments, runs, and the organisms, instruments, protocols, data
files, and organizations they reference — connected by labeled relations drawn from a
single controlled vocabulary. Shared entities collapse to one node, so a taxon or an
institution referenced by many records is represented once and linked from everywhere it
appears.

Each node carries two complementary layers: structural properties set by per-entity
converters, and a semantic annotation overlay derived from the structural ontology, so
every leaf value of a source record is tagged with what it means. The resulting graph can
be serialized to **GraphML** for desktop network tools (Gephi, yEd, Cytoscape) and to a
self-contained **interactive HTML** viewer. The package can also ingest supplementary
material — related papers and count data — into the same graph.

Built on [`BioFSharp.IO.INSDC`](https://www.nuget.org/packages/BioFSharp.IO.INSDC).

Part of [BioFSharp.INSDC](https://github.com/BioFSharp/BioFSharp.INSDC). Released under the MIT license.
