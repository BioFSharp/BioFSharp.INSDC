# Per-entity conversion restructure (BioProject exemplar)

> **Status: DONE (2026-08-20).** The original acceptance criteria are implemented. This document is retained as historical design context; future evolution is governed by [the active implementation plan](../implementation.md).

## Goal

Give each accession type its own conversion, authored explicitly so **object integrity is preserved** —
composite value-objects fold to a single annotation/edge instead of being shredded into independent
leaves. **Decouple** the mapping from the structural-ontology decompilation for now: the decompilation
stays in the tree (still valuable as a future *term provider*) but is no longer wired into the converter.
Done for **BioProject only**; the other seven entities keep the decompilation overlay (intentional mixed
transitional state). This converter is the template for the rest.

Motivation: the flat decompilation (`Ontology.annotationsOf`) walks every leaf, so
`ExternalId { Namespace="BioProject"; Value="PRJDB5192"; Label="primary" }` becomes three unrelated
annotations. It should fold to one: term Name = `BioProject`, value = `PRJDB5192` (and, if the value
denotes a modelled entity, an edge too).

## Structure

```
Mapping/
  ArcValueConversion.fs   (shared, existing)
  Ontology.fs             (shared, existing — decompilation; NOT called by the new converter)
  SubObjects.fs           (shared, existing — node builders)
  Mapping.fs              (shared, existing — PendingRelation, resolver, build)
  Annotations.fs          (NEW shared — composite -> annotation folders)
  BioProject/BioProject.fs (NEW — module BioProjectConversion, the explicit converter)
  INSDC.fs                (existing — other 7 converters + INSDC.build; bioProject now delegates)
```

fsproj compile order: `ArcValueConversion → Ontology → SubObjects → Mapping → Annotations →
BioProject/BioProject → INSDC`. `Annotations.fs` sits after `Mapping.fs` because `identifierAnnotations`
returns `PendingRelation` (defined there). `INSDC.bioProject` becomes a one-line delegate to
`BioProjectConversion.convert`, so the smoke script, `INSDC.build`, and tests keep working.

## Shared `Annotations.fs`

Move `attributeAnnotations` + `slug` out of `INSDC.fs` (a file compiled before `INSDC` can't reach into
it; the other converters switch to `Annotations.attributeAnnotations`). Add:

- `field source key value` — one scalar field as an annotation (term Name = key, id derived from the key).
- `attributeAnnotations attrs` — moved; unchanged behaviour (tag→term, value+units→value).
- `identifierAnnotations subjectId (ids: Identifier) : ArcAnnotation list * PendingRelation list`:
  - `PrimaryId`/`SecondaryId`/`Uuid` (`Name`) → annotation term `primaryId`/`secondaryId`/`uuid`, value `.Value`.
  - `ExternalId`/`SubmitterId` (`QualifiedName`) → annotation term = **`.Namespace`**, value = **`.Value`** (`Label` dropped for now).
  - **Edge if possible:** for a `QualifiedName` whose `Namespace` denotes a modelled entity
    (BioProject/Study/Sample/Experiment/Run/Analysis/Submission) and whose value ≠ the subject, emit a
    `PendingRelation subjectId --references--> value` (existing resolver → dangles if the target isn't
    loaded; self-references skipped).

Synthetic term ids under `http://purl.org/arc/insdc/…` with positional disambiguation (several values can
share a key). `Source` tags: `INSDC identifier`, `INSDC attribute`, `INSDC BioProject`.

## BioProject converter (`BioProjectConversion.convert`)

No `Ontology.annotationsOf`. Produces a `Collection` node (DTypes `bioProject` + `investigation`), empty
`Properties`, and:

| BioProject field | ArcIR |
|---|---|
| Accession | node `Id` (also surfaces as a `primaryId` annotation via Identifiers) |
| Alias, Name, Title, Description, FirstPublic | **annotations** (`Annotations.field`; date as datetime literal) |
| CenterName, BrokerName | Agent nodes + edges (`SubObjects.organization`) |
| ProjectAttributes | attribute annotations (`Annotations.attributeAnnotations`) |
| Identifiers | identifier annotations + optional `references` edges (`Annotations.identifierAnnotations`) |
| RelatedProjects | parent/child/peer edges (local `pendingAccession`) |

## Vocabulary

Add `Vocabulary.Rel.references` (identifier → entity edges).

## INSDC.fs

Remove the `attributeAnnotations`/`slug` helpers and the `bioProject` body; add
`let bioProject p = BioProjectConversion.convert p`; qualify the six remaining `attributeAnnotations`
calls as `Annotations.attributeAnnotations`.

## Tests

- Unit (`Annotations.identifierAnnotations`, constructed `Identifier`): an `ExternalId` folds to one
  namespaced annotation (`Namespace` → term Name, `Value` → literal); a modelled-entity id yields a
  `references` `PendingRelation`; a self-reference yields none.
- Fixture (`ir`): the BioProject node has **no** shredded decompilation leaves (no annotation whose Name
  starts with `BioProject.`), and its `Properties` are empty.
- Existing BioProject facts still hold: `Collection` kind, `bioProject` DType, related-project edge,
  shared-institution dedup (`org:ddbj`).

## Build & verify

```
./build.sh runtests
dotnet build src/BioFSharp.INSDC.ArcIR && dotnet fsi playground/arcir_graphml.fsx
```
Inspect the BioProject node in `playground/arcir.html`: `ExternalId` shows as `BioProject = PRJDB5192`
(one row), no `.Identifiers.ExternalId.Namespace/.Value/.Label` leaves.

## Status

Rolled out to **all** entities: shared helpers extracted to `Mapping/Convert.fs`, and every entity has
its own explicit, decompilation-decoupled converter under `Mapping/<Entity>/<Entity>.fs`
(BioProject/BioSample/Study/Experiment/Run/Analysis/Submission/Receipt). `INSDC.fs` is now a thin facade.
No entity node carries flat decompilation leaves; a Study's `ExternalId namespace="BioProject"` folds to
one `BioProject = <accession>` annotation **and** a `references` edge to that BioProject.

## Future work

Reconnect the structural ontology as a **term provider** (field/xpath → `OntologyTerm`) so explicit
annotations carry real ontology terms instead of the current synthetic ones; consider a type-directed
registry if the folder set grows; fold the remaining composites (`XrefLink`/`UrlLink` in `*_LINKS`).

## Key files

- New: `Mapping/Annotations.fs`, `Mapping/BioProject/BioProject.fs`
- Edit: `Vocabulary.fs`, `Mapping/INSDC.fs`, `BioFSharp.INSDC.ArcIR.fsproj`, `tests/.../Tests.fs`
