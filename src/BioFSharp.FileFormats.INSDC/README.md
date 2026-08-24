# BioFSharp.FileFormats.INSDC

The generated C# type model for [INSDC](https://www.insdc.org/) XML records and
the foundation of the `BioFSharp.INSDC` package suite.

The types are generated directly from the committed ENA/SRA 1.5 schemas and
cover BioProject, Study, Sample, Experiment, Run, Analysis, Submission, and
Receipt. `FragmentSelectors.cs` is generated from the same serialization
metadata and records the XPath/XPointer location of record fields for structural
inspection in higher layers.

All C# under `Generated/` and `FragmentSelectors.cs` is generator-owned.
Type-name changes belong in `schemas/typename-substitutions.txt`, never in
generated source. From the repository root, regenerate and verify in this order:

```bash
./build.sh regenerateInsdcTypes
./build.sh generateFragmentSelectors
./build.sh generateStructuralOntology
./build.sh verifyGeneratedArtifacts
```

Use the matching `build.cmd` targets on Windows. The generators sort inputs and
normalize headers, encoding, and line endings so the drift gate is
byte-reproducible.

Most applications reference
[`BioFSharp.IO.INSDC`](https://www.nuget.org/packages/BioFSharp.IO.INSDC)
instead of consuming the generated types directly.

Part of [BioFSharp.INSDC](https://github.com/BioFSharp/BioFSharp.INSDC).
Released under the MIT license.
