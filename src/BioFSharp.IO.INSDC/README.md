# BioFSharp.IO.INSDC

Idiomatic F# reading and writing for
[INSDC](https://www.insdc.org/) XML records, built on the generated
[`BioFSharp.FileFormats.INSDC`](https://www.nuget.org/packages/BioFSharp.FileFormats.INSDC)
type model.

BioProject, Study, BioSample, Experiment, Run, Analysis, Submission, and Receipt
each have a module exposing exactly `read`, `readString`, `write`, and
`writeString` for file- and string-based XML round trips.

The package can also report the XPath or W3C XPointer of fields on a parsed
instance and decompile a record into structural term/value pairs whose names
mirror the XML shape. These APIs describe source structure for inspection and
provenance. They are not a semantic annotation overlay for the ArcIR mapping;
the current ArcIR converters map record fields explicitly.

`StructuralOntology.obo` is generated from the same committed schemas and
fragment-selector metadata as the C# model. Regenerate it only through the FAKE
generator targets documented in the repository root README.

Part of [BioFSharp.INSDC](https://github.com/BioFSharp/BioFSharp.INSDC).
Released under the MIT license.
