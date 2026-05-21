### 0.1.0 (2026-05-21)

initial release of the generated C# type model and F# wrapper for the INSDC file formats.
The type model covers all schemas from https://ftp.ebi.ac.uk/pub/databases/ena/doc/xsd/sra_1_5, but IO only supports the most common ones for now:

- BioProject
- Study
- BioSample
- Experiment
- Run
- Submission
- Analysis

### 0.0.0-preview.1 (Unreleased)

* Initial repository scaffolding for BioFSharp.INSDC.
* Two-package layout established: `BioFSharp.FileFormats.INSDC` (C# generated type model) and `BioFSharp.IO.INSDC` (F# wrapper).
