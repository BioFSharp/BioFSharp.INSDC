# BioFSharp.INSDC.Crawler

Collects [INSDC](https://www.insdc.org/) (International Nucleotide Sequence Database
Collaboration) records from the [ENA](https://www.ebi.ac.uk/ena/) public archive, as part
of the [`BioFSharp.INSDC`](https://github.com/BioFSharp/BioFSharp.INSDC) suite.

Given a single project accession, the crawler discovers every run, experiment, sample,
and study connected to it, fetches the corresponding records from ENA, and persists them
— together with the cross-reference graph that links them — through the
[`BioFSharp.INSDC.SQLite`](https://www.nuget.org/packages/BioFSharp.INSDC.SQLite) store.
The result is a local, queryable snapshot of a complete study assembled from one starting
accession.

Because it performs live HTTP, this package targets .NET 6+ (net8.0) rather than the
netstandard2.0 baseline of the rest of the suite.

Part of [BioFSharp.INSDC](https://github.com/BioFSharp/BioFSharp.INSDC). Released under the MIT license.
