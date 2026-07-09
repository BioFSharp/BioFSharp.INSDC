# Test fixtures

These XML files are real INSDC records used as inputs for the `BioFSharp.INSDC.Tests` suite. They are downloaded once by hand and committed so tests stay deterministic and run offline.

Do not auto-fetch from this directory at test time.

## Source

All entity records come from the ENA Browser API:

```
https://www.ebi.ac.uk/ena/browser/api/xml/<ACCESSION>
```

Downloaded 2026-05-21.

| Fixture                | Entity     | Source URL                                                  |
| ---------------------- | ---------- | ----------------------------------------------------------- |
| `PRJDB5192.xml`        | BioProject | <https://www.ebi.ac.uk/ena/browser/api/xml/PRJDB5192>       |
| `DRP003416.xml`        | Study      | <https://www.ebi.ac.uk/ena/browser/api/xml/DRP003416>       |
| `SAMD00064197.xml`     | BioSample  | <https://www.ebi.ac.uk/ena/browser/api/xml/SAMD00064197>    |
| `DRX066772.xml`        | Experiment | <https://www.ebi.ac.uk/ena/browser/api/xml/DRX066772>       |
| `DRR072834.xml`        | Run        | <https://www.ebi.ac.uk/ena/browser/api/xml/DRR072834>       |
| `ERZ496533.xml`        | Analysis   | <https://www.ebi.ac.uk/ena/browser/api/xml/ERZ496533>       |
| `DRA005154.xml`        | Submission | <https://www.ebi.ac.uk/ena/browser/api/xml/DRA005154>       |
| `receipt-sample.xml`   | Receipt    | Hand-crafted — `RECEIPT` is a submission-API response document and has no accession-based endpoint. The shape mirrors the example response in the ENA programmatic submission guide. |

## Ingest fixtures

Inputs for the supplementary-source ingestion tests (see `plans/arcir-ingest.md`). Hand-crafted, not real records: a paper describing `PRJDB5192` and a count matrix whose columns are `DRR*` run accessions (`DRR072834` is the `DRR072834.xml` run above; `DRR072835` is deliberately absent, to exercise a dangling `producesData` edge).

| Fixture                     | Kind                | Notes                                                                                     |
| --------------------------- | ------------------- | ----------------------------------------------------------------------------------------- |
| `paper-PRJDB5192.jats.xml`  | Paper (JATS XML)    | Minimal JATS: title, DOI, journal, two authors (name/email/ORCID/affiliation).            |
| `counts-PRJDB5192.tsv`      | Count matrix (TSV)  | Header `gene_id`, `DRR072834`, `DRR072835`; a few gene rows.                               |
| `counts-PRJDB5192.zip`      | Count matrix (zip)  | The same `counts-PRJDB5192.tsv` zipped, to exercise the archive reader (regenerate below). |

Regenerate the zip after editing the TSV:

```bash
cd tests/fixtures && python3 -c "import zipfile; zipfile.ZipFile('counts-PRJDB5192.zip','w',zipfile.ZIP_DEFLATED).write('counts-PRJDB5192.tsv', arcname='counts-PRJDB5192.tsv')"
```

## Refreshing a fixture

```bash
curl -sf "https://www.ebi.ac.uk/ena/browser/api/xml/<ACCESSION>" -o tests/fixtures/<ACCESSION>.xml
```

If you replace a fixture, re-run `bash build.sh runtests` and update any field-value assertions in `tests/BioFSharp.INSDC.Tests/Tests.fs` that depended on the old payload.
