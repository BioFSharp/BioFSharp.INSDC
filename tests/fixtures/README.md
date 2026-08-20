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

## Paper-discovery fixtures

Input for the EuropePMC paper auto-discovery tests (`PaperDiscoveryTests`): a real `search` response that maps a PubMed id to its PMCID.

| Fixture                             | Kind                  | Notes                                                                                          |
| ----------------------------------- | --------------------- | ---------------------------------------------------------------------------------------------- |
| `europepmc-search-18808718.json`    | EuropePMC search JSON | Resolves PMID `18808718` (a real `<DB>PUBMED</DB>` xref on BioProject `PRJNA106377`) → `PMC2568001`. |

Refresh:

```bash
curl -sf "https://www.ebi.ac.uk/europepmc/webservices/rest/search?query=EXT_ID%3A18808718%20AND%20SRC%3AMED&format=json&resultType=lite&pageSize=1" -o tests/fixtures/europepmc-search-18808718.json
```

## Real vs. synthetic — read this before trusting a green test

Several fixtures are **hand-crafted**, not real service payloads. A test passing against a made-up
fixture proves the parsing and the wiring, **not** that the code survives what the service actually
returns. That distinction is not academic — it hid a live bug (see below).

| Fixture | Real? |
|---|---|
| `PRJDB5192.xml`, `DRP003416.xml`, `SAMD00064197.xml`, `DRX066772.xml`, `DRR072834.xml` | ✅ real ENA responses |
| `crawl-PRJDB5192.filereport.tsv`, `europepmc-search-18808718.json` | ✅ real |
| `dee2-search-DRP003416.html` | ✅ real DEE2 `search2.sh` response |
| `paper-PRJDB5192.jats.xml` | ⚠️ minimal, hand-written |
| `paper-PRJDB5192.pdf` | ⚠️ a 237-byte `%PDF-1.4` stub, not an article |
| `dee2-DRP003416.zip` / `.tsv` | ⚠️ a one-file toy zip; a real bundle is ~3.7 MB with several files |
| `counts-PRJDB5192.tsv` / `.zip` | ⚠️ minimal |

**What the synthetic fixtures cost us.** The paper PDF used to be fetched from EuropePMC's
`fullTextPDF` endpoint, which **404s for every article** — so `PaperResult.Pdf` was unreachable in
production. Every offline test passed regardless, because the stub simply handed back the fake PDF
bytes; a wholly fictitious URL would have passed too. PDFs now come from the PMC Open Access dataset
on AWS (`pmc-oa-opendata`), and a `LIVE` opt-in test actually downloads one and checks the `%PDF-`
magic. Similarly, the old hand-written DEE2 search fixture claimed
`http://dee2.io/huge/athaliana/DRP003416.zip`; the real page serves
`https://…/DRP003416_NA.zip` — HTTPS, and an `_NA` suffix.

**Prefer a real fixture.** If you must fabricate one, say so here, and back it with a
`LIVE`-gated test (`INSDC_LIVE_TESTS=1`) that hits the real service.

## Refreshing a fixture

```bash
# ENA record
curl -sf "https://www.ebi.ac.uk/ena/browser/api/xml/<ACCESSION>" -o tests/fixtures/<ACCESSION>.xml

# DEE2 accession search (real response; note the link is https and may carry an _NA suffix)
curl -sfL "http://dee2.io/cgi-bin/search2.sh?org=athaliana&accessionsearch=DRP003416" \
  -o tests/fixtures/dee2-search-DRP003416.html
```

If you replace a fixture, re-run `bash build.sh runtests` and update any field-value assertions in `tests/BioFSharp.INSDC.Tests/Tests.fs` that depended on the old payload.
