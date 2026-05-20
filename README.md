# BioFSharp.INSDC

![Logo](docs/img/Logo_large.png)

Read/write support for [INSDC](https://www.insdc.org/) XML records — BioProject, Study, Sample, Experiment, Run, Analysis, Submission, Receipt — as a direct dependency of [BioFSharp](https://github.com/CSBiology/BioFSharp).

## Packages

| Package                       | Purpose                                                                                                                                                          |
| ----------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `BioFSharp.FileFormats.INSDC` | C# type model auto-generated from the [ENA SRA XSDs](https://ftp.ebi.ac.uk/pub/databases/ena/doc/xsd/sra_1_5/) via [`dotnet-xscgen`](https://www.nuget.org/packages/dotnet-xscgen). |
| `BioFSharp.IO.INSDC`          | F# wrapper exposing `read` / `readString` / `write` / `writeString` per INSDC entity.                                                                              |

The C# split exists because there is no F# equivalent of `XmlSchemaClassGenerator`. Both packages target `netstandard2.0`.

## Repo layout

```text
.
├── build/                                  FAKE build project
├── docs/                                   Placeholder — no fsdocs site is published from this repo
├── plans/implementation.md                 Authoritative implementation plan
├── src/
│   ├── BioFSharp.FileFormats.INSDC/        C# generated type model
│   │   ├── schemas/                          Committed ENA XSDs
│   │   └── Generated/                          Tool output — do not hand-edit
│   └── BioFSharp.IO.INSDC/                 F# wrapper
└── tests/BioFSharp.INSDC.Tests/            xunit tests, with committed ENA fixtures
```

## Build

First-time setup:

```bash
dotnet tool restore     # installs the pinned dotnet-xscgen
```

Then:

```bash
build.cmd               # Windows
./build.sh              # macOS / Linux
```

Other targets:

```bash
build.cmd runtests
build.cmd pack
build.cmd regenerateInsdcTypes   # only when the XSDs change
```

## Contributing

See [`AGENTS.md`](AGENTS.md) for repo conventions and [`plans/implementation.md`](plans/implementation.md) for the implementation roadmap.

Documentation lives in the [base BioFSharp docs](https://csbiology.github.io/BioFSharp/) rather than in this repo.
