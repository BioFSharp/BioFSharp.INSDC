# BioFSharp.INSDC.ArcIR

Maps [INSDC](https://www.insdc.org/) records into the repository's current
proof-of-concept **ArcIR** property graph.

Parsed records become typed, related objects - projects, studies, samples,
experiments, runs, organisms, instruments, protocols, data files, and
organizations. Explicit per-entity converters create graph properties and
annotations; they do not consume the IO package's structural decompilation.
Shared entities collapse to one object and labeled relations connect them.

The graph can be rendered as text, GraphML, or a self-contained HTML page and
can ingest supplementary papers and count data. Those renderers are derived
inspection tools for the current model. In particular, the embedded HTML page
is not the intended final workbench architecture.

The authoritative repository roadmap will replace these proof-of-concept shapes
in a breaking change: a target-neutral `BioFSharp.ArcIR` core will own the
canonical graph, while this package becomes the INSDC-specific F1 adapter. In
that terminology, F1 ingests source metadata, curation produces later IR
revisions, and F2 compiles a selected revision without mutating it. No concrete
production F2 target exists yet.

Built on
[`BioFSharp.IO.INSDC`](https://www.nuget.org/packages/BioFSharp.IO.INSDC).

Part of [BioFSharp.INSDC](https://github.com/BioFSharp/BioFSharp.INSDC).
Released under the MIT license.
