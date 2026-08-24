# BioFSharp.INSDC.SQLite

A versioned SQLite store for [INSDC](https://www.insdc.org/) records.

The store deconstructs BioProject, Study, BioSample, Experiment, and Run values
into a normalized relational schema and reconstructs the original record on
read. An `accession_relations` table records cross-record connectivity even when
a referenced record was not downloaded.

`Schema.init` creates a new database or applies every ordered forward migration
to a supported existing database. `Schema.currentVersion` identifies the newest
version understood by the package. Each migration and each public entity insert
or delete operation is transactional.

Foreign keys are enforced by default. `Schema.setForeignKeyMode` makes the one
exception explicit: the crawler selects `AllowCrawlerSoftReferences` because
ENA can identify the same biological sample with distinct SRA and BioSample
accessions, and partial crawls can legitimately retain only one side. Ordinary
store consumers should keep `Enforce`.

Built on
[`BioFSharp.IO.INSDC`](https://www.nuget.org/packages/BioFSharp.IO.INSDC) and
used by the
[`BioFSharp.INSDC.Crawler`](https://www.nuget.org/packages/BioFSharp.INSDC.Crawler).

Part of [BioFSharp.INSDC](https://github.com/BioFSharp/BioFSharp.INSDC).
Released under the MIT license.
