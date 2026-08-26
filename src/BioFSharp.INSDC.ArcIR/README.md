# BioFSharp.INSDC.ArcIR

Maps [INSDC](https://www.insdc.org/) records into the target-neutral
[`BioFSharp.ArcIR`](../BioFSharp.ArcIR/README.md) property graph.

Parsed records become typed, related objects - projects, studies, samples,
experiments, runs, organisms, instruments, protocols, data files, and
organizations. Explicit per-entity converters create graph properties and
annotations; they do not consume the IO package's structural decompilation.
Shared entities are combined through explicit conflict-reporting merge semantics
and labeled relations connect them. Bare accessions and aliases are converted to
absolute adapter URNs; F1 assertions and relations receive deterministic IDs that
do not depend on their current values.

All objects, type assertions, properties, annotations, and relations are stored
in identity-keyed maps. Their predicates and values reference one shared graph-
level term registry. The adapter covers BioProject, Study, BioSample, Experiment,
Run, Analysis, Submission, and Receipt, plus supplementary papers and count data.

The graph can be rendered as text, GraphML, or a self-contained HTML page. These
are derived inspection tools; the embedded HTML page is not the intended final
workbench architecture. Deterministic canonical persistence and revision-bound
fragment addressing are planned separately.

Built on
[`BioFSharp.IO.INSDC`](https://www.nuget.org/packages/BioFSharp.IO.INSDC).

Part of [BioFSharp.INSDC](https://github.com/BioFSharp/BioFSharp.INSDC).
Released under the MIT license.
