# BioFSharp.ArcIR

`BioFSharp.ArcIR` is the target-neutral graph core used by BioFSharp metadata
adapters. It contains validated absolute IRI identities, a normalized property
graph, explicit lossless graph operations, structural validation, and
format-neutral persistence contracts.

The package does not reference INSDC schemas, crawler or SQLite code, SSSOM, or
any output target. Source-specific ingestion belongs in adapter packages such as
`BioFSharp.INSDC.ArcIR`.

All independently curatable elements are keyed by stable `Iri` identities.
Ontology term definitions are shared through `ArcIR.Terms`; assertions reference
those definitions rather than embedding copies.
