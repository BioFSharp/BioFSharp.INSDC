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

## Generated type naming

`dotnet xscgen` derives C# type names mechanically from the XSDs, which produces verbose names like `AnalysisTypeAnalysisTypeTranscriptomeAssembly`. We clean these up via [`src/BioFSharp.FileFormats.INSDC/schemas/typename-substitutions.txt`](src/BioFSharp.FileFormats.INSDC/schemas/typename-substitutions.txt), passed to the tool with `--tnsf`. The substitution file:

- Has one rule per line in the form `A:<xscgen-default-name>=<substitute>` (the `A:` prefix matches any type/member; lines starting with `#` are comments).
- Documents its naming rules (A–F) in a header block — read those before adding rules so renames stay consistent.
- Is the **only** place to change a generated type's name; never hand-edit files under `Generated/`.

To add or change a substitution:

1. Edit `typename-substitutions.txt`. The left side is the name xscgen would emit without any substitution (the original XSD-derived path); the right side is the desired C# identifier. Pick a substitute that does not collide with another type — xscgen falls back to a generic name (e.g. `<Name>Item`) if the substitute clashes with an existing default.
2. Run `build.cmd regenerateInsdcTypes` (or `./build.sh regenerateInsdcTypes`).
3. Commit both the updated substitution file and the regenerated files under `src/BioFSharp.FileFormats.INSDC/Generated/`.

Caveats:

- xscgen's substitution file does not accept regex or dotted/nested names — `Foo.Bar` would emit invalid C# (`class Foo.Bar`). Substitutes must be flat C# identifiers.
- Stay consistent with the rules already documented in the file's header. If a rename does not fit any existing rule, add a new lettered rule alongside the others.

## Contributing

See [`AGENTS.md`](AGENTS.md) for repo conventions and [`plans/implementation.md`](plans/implementation.md) for the implementation roadmap.

Documentation lives in the [base BioFSharp docs](https://csbiology.github.io/BioFSharp/) rather than in this repo.
