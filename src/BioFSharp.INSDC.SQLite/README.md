# BioFSharp.INSDC.SQLite

A SQLite-backed store for [INSDC](https://www.insdc.org/) (International Nucleotide
Sequence Database Collaboration) records, part of the
[`BioFSharp.INSDC`](https://github.com/BioFSharp/BioFSharp.INSDC) suite.

The store deconstructs parsed BioProject, Study, BioSample, Experiment, and Run values
into a normalized relational schema and reconstructs the original records on read, so a
collection of INSDC entities can be persisted, queried, and round-tripped through an
ordinary SQLite database file.

Alongside the per-entity tables it maintains an *accession relations* table that captures
how records connect to one another — the cross-references linking a project to its
studies, samples, experiments, and runs — so the connectivity of a dataset is queryable
as a graph rather than only implied by the records.

Built on [`BioFSharp.IO.INSDC`](https://www.nuget.org/packages/BioFSharp.IO.INSDC) and
used by the [crawler](https://www.nuget.org/packages/BioFSharp.INSDC.Crawler) to persist
what it collects.

Part of [BioFSharp.INSDC](https://github.com/BioFSharp/BioFSharp.INSDC). Released under the MIT license.
