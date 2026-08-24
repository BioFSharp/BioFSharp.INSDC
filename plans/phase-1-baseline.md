# Phase 1 stabilization baseline

Recorded on 2026-08-24 before Phase 1 changes, using the repository's FAKE
`RunTests` target on Windows.

## Environment and inventory

| Item | Baseline |
| --- | --- |
| Selected SDK | .NET SDK 10.0.400 (`global.json` requests 10.0.100 with `latestMinor` roll-forward) |
| Local tools | 2: `fsdocs-tool` 20.0.1 and `dotnet-xscgen` 3.0.1270 |
| Solution projects | 7: five shipped projects, one test project, and the FAKE build project |
| Target frameworks | four shipped libraries on `netstandard2.0`; crawler on `net8.0`; tests and build project on `net10.0` |
| Resolved top-level package references | 33 across project/framework pairs, representing 21 unique package IDs |
| Generated C# files | 280 under `BioFSharp.FileFormats.INSDC/Generated/` |
| Tracked files | 455 |
| Offline tests | 149 passed, 0 failed |

## Baseline findings

The baseline build was green but restore/audit output reported known vulnerable
transitive packages. The first actionable chain was
`Microsoft.Data.Sqlite 8.0.10` to `SQLitePCLRaw.lib.e_sqlite3 2.1.6`. The FAKE
build dependency graph also resolved vulnerable NuGet client and drawing
packages. Restore warnings did not fail the original `RunTests` target, and no
generated-artifact drift check existed.

This record deliberately captures the pre-change state. Phase 1 acceptance is
performed through the updated FAKE `RunTests` target, which now includes exact
dependency auditing and byte-level generated-output verification before the
offline tests run.

## Acceptance result

On 2026-08-24, the final `build.cmd RunTests` execution completed successfully:

- solution build: 0 warnings and 0 errors;
- dependency audit: 0 vulnerabilities and 0 suppressions;
- generated types, fragment selectors, and structural ontology: no drift;
- offline suite: 173 passed, 0 failed.
