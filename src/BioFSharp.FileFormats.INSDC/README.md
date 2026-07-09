# BioFSharp.FileFormats.INSDC

The C# type model for [INSDC](https://www.insdc.org/) (International Nucleotide Sequence
Database Collaboration) sequence-database records, and the foundation of the
[`BioFSharp.INSDC`](https://github.com/BioFSharp/BioFSharp.INSDC) suite.

The types are generated directly from the official ENA/SRA XML schemas, so the model
stays faithful to the upstream standard and covers the full record set — BioProject,
Study, Sample, Experiment, Run, Analysis, Submission, and Receipt. Generation is
mechanical and repeatable, so the model is regenerated rather than hand-maintained when
the schemas change.

The package also carries the generated *fragment selectors*: for every field of every
record it records the exact XPath/XPointer location that field occupies in the source
XML, derived from the same serialization metadata the parser uses. Higher layers of the
suite build on this to address, track, and semantically annotate individual values
without ever drifting from the type model.

This package is consumed by [`BioFSharp.IO.INSDC`](https://www.nuget.org/packages/BioFSharp.IO.INSDC)
and the rest of the suite; most users depend on those higher-level packages rather than
referencing the type model directly.

Part of [BioFSharp.INSDC](https://github.com/BioFSharp/BioFSharp.INSDC). Released under the MIT license.
