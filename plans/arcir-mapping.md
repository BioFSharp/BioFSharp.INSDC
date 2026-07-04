# INSDC → ArcIR mapping plan

## Overview

`BioFSharp.INSDC.ArcIR` maps parsed INSDC records into **ArcIR** — an ARC-oriented intermediate
representation shaped as an RDF/property graph:

- nodes = `ArcObject` (keyed by `ArcId`, a single closed `Kind` + open `DTypes: Set<Iri>` + `Properties` + ontology-backed `Annotations`),
- edges = `ArcRelation` (subject–predicate–object triples).

**Approach (settled).** Two candidate inputs were weighed: (A) the flat structural-ontology
decompilation (`DecompiledTerm list`), and (B) per-entity typed converters. ArcIR is a *graph of typed,
related objects*; the decompilation deliberately *flattens* that structure into string leaves. So the
mapping uses a **hybrid**:

- **Structure** (objects, typed values, sub-objects, edges) comes from **per-entity typed converters** (approach B).
- **Semantics** (a per-leaf, ontology-termed annotation layer) comes from the **decompilation overlay** (approach A), reused via `Ontology.annotationsOf`.

Scope decisions for the full build-out:

- **Entities:** the 8 IO-readable entities (BioProject, Study, BioSample, Experiment, Run, Analysis, Submission, Receipt). The other 7 roots (Dataset/Policy/Dac/Checklist/Assembly/SampleGroup/Webin) have no IO `read`/`decompile` and are out of scope; edges to them dangle until such records exist.
- **Rich sub-objects:** Agents (people/centers), Instruments (platform), Protocols (library), and Data files are first-class nodes with edges — not just properties.
- **Formalize first:** a controlled predicate + DType **IRI vocabulary** and finalized `ArcObjectKind` names precede the converters.

## Design

### ArcObjectKind (finalized)

| Kind | INSDC concepts |
|---|---|
| Collection | BioProject (Investigation), Study, Submission bundle |
| Observable | BioSample (material), Organism/Taxon |
| Activity | Experiment (assay), Run, Analysis, Receipt |
| Instrument | Platform / instrument model |
| Recipe | Library descriptor (protocol) |
| Agent | Contacts, center/broker/lab institutions |
| Role | An agent's role (reserved) |
| Resource | Data files, external links |
| Selector | XPath provenance nodes (reserved) |

### Vocabulary (`Vocabulary.fs`)

Single source of truth for predicate + DType identity, minted as IRIs under a documented base
(`http://purl.org/arc/insdc#`, placeholder pending registration). `Vocabulary.Rel.*` predicates,
`Vocabulary.DType.*` types. Field-level property keys are intentionally **not** formalized — the
ontology-termed identity of every leaf already lives in the annotation overlay.

### Reference matrix (edges emitted)

| Owner | Property (card.) | Predicate | Target |
|---|---|---|---|
| Experiment | `StudyRef` (1) | hasStudy | Study |
| Experiment | `Design.SampleDescriptor` (1) + `.Pool.Member[]` | hasSample | BioSample |
| Run | `ExperimentRef` (1) | hasExperiment | Experiment |
| Analysis | `StudyRef` (1), `SampleRef[]`, `ExperimentRef[]`, `RunRef[]`, `AnalysisRef[]` | hasStudy/hasSample/hasExperiment/hasRun/hasAnalysis | Study/Sample/Experiment/Run/Analysis |
| BioProject | `RelatedProjects` parent/child/peer | hasParentProject/hasChildProject/hasPeerProject | BioProject |
| Receipt | `Id` buckets per entity type | acknowledges | any |

Sub-object edges: `hasOrganism`, `usesInstrument`, `hasProtocol`, `producesData`, `hasContact`.

### Dedup

Sub-object ids are deterministic so shared things collapse to one node via `ArcIR.addObject`'s
merge-on-id: taxon → `taxon:<id>`, instrument → `instrument:<model>`, agent → `agent:<email|name+org>`,
organization → `org:<name>`.

## Phased plan & status

### 1. [x] Foundation: project + v1 slice

New packable project `src/BioFSharp.INSDC.ArcIR` (netstandard2.0), registered in
`BioFSharp.INSDC.slnx` and referenced by the test project. Core types (`ArcCore.fs`, `ArcIR.fs`,
`ArcObject.fs`) promoted from `playground/`. v1 infra + converters for BioProject/BioSample/Experiment,
5 regression tests.

### 2. [x] Vocabulary & kind finalization

- `Vocabulary.fs` — `Rel` (predicates) + `DType` (types) as IRIs.
- `ArcObjectKind` committed to the 9 cases above (provisional caveat dropped).

### 3. [x] Shared builders & resolver extensions

- `Mapping/SubObjects.fs` — `organism`, `dacContact`/`submissionContact`/`organization` (Agents), `instrument` (reflects the `Platform` choice), `protocol` (LibraryDescriptor), `analysisFile`/`runFile` (Resources); deterministic dedup ids.
- `Mapping/ArcValueConversion.fs` — generic `ofEnum`/`ofEnumObj` mapping any of the 57 INSDC enums to `ArcValue.Iri` under an enum-vocabulary base.
- `Mapping/Mapping.fs` — resolver made refcenter-aware (`refname` resolves within `refcenter` namespace, falling back to bare alias then a synthetic id).

### 4. [x] Per-entity converters (`Mapping/INSDC.fs`)

All 8 converters (`INSDC.bioProject/study/bioSample/experiment/run/analysis/submission/receipt`) plus
`INSDC.build`. The three existing converters were retrofitted to the vocabulary + builders. Notable:
Experiment gains Instrument + Protocol sub-objects and pool-member sample edges; Analysis is the ref hub
(five edge families) + data files; Submission builds Agent nodes from contacts; Receipt is bespoke (not
an INSDC `Object`) and emits `acknowledges` edges.

**Builds clean** (`dotnet build src/BioFSharp.INSDC.ArcIR`). End-to-end smoke
(`dotnet fsi playground/ir_mapping.fsx`, one fixture per entity) yields a **graph, not a flat bag**:

- 17 objects across all 8 entity kinds + all 4 sub-object kinds (Instrument, Recipe, Resource, Agent).
- 26 relations: hasStudy, hasSample, hasExperiment, usesInstrument, hasProtocol, producesData, hasParentProject, hasOrganism, acknowledges, hasContact.
- Typed values (e.g. taxon id as `Integer`, dates as `DateTime`), enums as vocabulary IRIs.
- Dedup verified: `org:ddbj` shared across 6 entities; `taxon:3702` shared.

### 5. [x] Tests & verification (package README still optional)

- [x] `ArcMappingTests` retrofitted to the vocabulary constants + dedup ids and expanded to **11 facts** over all 8 entities: identity + kind, vocabulary IRIs, typed integer taxon (deduped node), experiment → study/sample/instrument/protocol, run → experiment, analysis → study + data-file resources, bioproject related-project, receipt `acknowledges` + typed `Success`/`ReceiptDate`, shared-institution dedup, enum → `ArcValue.Iri`, ontology annotations.
- [x] `ArcResolverTests` (**3 facts**) over the resolve-afterwards pass: accession-direct (target not loaded), refcenter-namespaced refname → loaded object, synthetic-id fallback.
- [x] `./build.sh runtests` green — **63/63** on a clean full-solution rebuild.
- [ ] A package README (this file already serves as the design + vocabulary reference).

## Deferred / future

- **Non-IO roots** (Dataset/Policy/Dac/Checklist/Assembly/SampleGroup/Webin): need `read`/`decompile` in `BioFSharp.IO.INSDC` first; their edges currently dangle. SampleGroup refs also need a non-`RefObject` resolver path (`IRefNameGroup`).
- **Provenance**: attach each annotation's XPath as a `Selector` node via `ArcAnnotation.Source` (TODO in `Ontology.fs`).
- **Vocabulary alignment**: map the local predicates/DTypes and enum terms to standard ontologies (PROV-O/RO/OBI/NCBITaxon/EDAM); optionally emit them from the FAKE structural-ontology generator (with a regression test).
- **Receipt id**: currently falls back to `SubmissionFile` (e.g. `submission.xml`) — prefer a stronger identifier when available.

## Build & verify

```
dotnet build src/BioFSharp.INSDC.ArcIR            # compile the mapping library
dotnet fsi   playground/ir_mapping.fsx            # smoke: print the assembled graph for 8 fixtures
./build.sh   runtests                             # full solution build + xUnit suite
```

## Key files

- Core model: `src/BioFSharp.INSDC.ArcIR/{ArcCore,ArcIR,ArcObject}.fs`
- Vocabulary: `src/BioFSharp.INSDC.ArcIR/Vocabulary.fs`
- Mapping: `src/BioFSharp.INSDC.ArcIR/Mapping/{ArcValueConversion,Ontology,SubObjects,Mapping,INSDC}.fs`
- Tests: `tests/BioFSharp.INSDC.Tests/Tests.fs` (`ArcMappingTests`)
- Smoke: `playground/ir_mapping.fsx`
