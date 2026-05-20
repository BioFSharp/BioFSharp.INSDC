# BioFSharp.INSDC implementation plan

## Overview

This repo will hold 2 main projects:

- `BioFSharp.FileFormats.INSDC`: A C# type model for INSDC records auto generated from xsd files via `XmlSchemaClassGenerator`
- `BioFSharp.IO.INSDC`: Small F# library to read and write INSDC records from and to files. This will be based on the C# type model from `BioFSharp.FileFormats.INSDC` and will be implemented as a wrapper around the C# types.

The naming is deliberate to integrate into the base `BioFSharp` namespace and library. The main reason is that we do not have an equivalent source generator for F#. Both libraries are intended as direct dependencies of BioFSharp.

## Implementation steps

1. Create the `BioFSharp.FileFormats.INSDC` project and set up the `XmlSchemaClassGenerator` (https://github.com/mganss/XmlSchemaClassGenerator) to generate C# types from the INSDC xsd files. It should be installed as dotnet tool in this repo. Include re-generation of the types into this projects build process.

2. Create the `BioFSharp.IO.INSDC` project and add a reference to `BioFSharp.FileFormats.INSDC`. Implement reading and writing of INSDC records from and to files. This will be implemented as a wrapper around the C# types from `BioFSharp.FileFormats.INSDC`. Follow BioFSharp parser convention:
    - namespace `BioFSharp.IO.INSDC`
    - modules for the types, e.g. `BioProject` or `Run` for the main API
    - basic parsers are named `readLines (lines: seq<string>)` and `read (filePath: string)` with the latter using the former under the hood.
    - writer functions are named `write (filePath: string)` and input types as needed, e.g. `BioProject` or `Run` records.

3. Add unit tests for the reading and writing functions in `BioFSharp.IO.INSDC`. Tests should include roundtrips and value extraction for all generated types.

## Dos

- ALWAYS add XML documentation to all public types and functions in both projects. This is crucial for maintainability and usability of the libraries.

## Dont's

- No docs page for this repo, example usage should be part of the base BioFSharp docs page.