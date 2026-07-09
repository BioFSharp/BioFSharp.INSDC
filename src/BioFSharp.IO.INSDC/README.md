# BioFSharp.IO.INSDC

F# reading and writing for [INSDC](https://www.insdc.org/) (International Nucleotide
Sequence Database Collaboration) sequence-database records — part of the
[`BioFSharp.INSDC`](https://github.com/BioFSharp/BioFSharp.INSDC) suite and a direct
companion to [BioFSharp](https://github.com/CSBiology/BioFSharp).

Each INSDC entity — BioProject, Study, Sample, Experiment, Run, Analysis, Submission,
and Receipt — is exposed as an F# module that parses the record from a file or string
and serializes it back to standard INSDC XML, on top of the generated
[`BioFSharp.FileFormats.INSDC`](https://www.nuget.org/packages/BioFSharp.FileFormats.INSDC)
type model.

Beyond plain IO, the package can report the precise XML location (XPath / W3C XPointer)
of any field on a parsed value, and can *decompile* a record into ontology term/value
pairs — pairing every leaf value with a structural-ontology term whose name mirrors the
record's XML structure. This location-and-meaning layer is what the mapping and store
packages build on to annotate and normalize records.

Part of [BioFSharp.INSDC](https://github.com/BioFSharp/BioFSharp.INSDC). Released under the MIT license.
