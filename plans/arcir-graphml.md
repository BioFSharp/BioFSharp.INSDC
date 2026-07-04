# ArcIR → GraphML visualization plan

## Overview

The INSDC → ArcIR mapping (`plans/arcir-mapping.md`) assembles a **property graph** —
`ArcIR { Objects: Map<ArcId, ArcObject>; Relations: Set<ArcRelation> }` (`ArcCore.fs`) — where nodes
carry a closed 9-case `Kind`, an open `DTypes` set, a typed `Properties` bag, and an `Annotations`
overlay, and edges are directed and labeled by a `Predicate` IRI. It is a general directed graph
(cyclic, multi-parent, dangling endpoints possible), not a tree.

Until now the only way to inspect a result was the `printfn` text dump in `playground/ir_mapping.fsx`.
This adds two serializers over the graph:

- a **GraphML serializer** (`GraphMl`) so a graph can be opened in **Gephi** (preferred) — or yEd /
  Cytoscape desktop — laid out with ForceAtlas 2, colored by `Kind`, with every property/annotation an
  inspectable column in the Data Laboratory;
- a **self-contained interactive HTML viewer** (`Html`) — a single offline page (embedded force-directed
  SVG, no external scripts/CDN) where nodes are colored by `Kind`, edges labeled by predicate, and
  clicking a node opens a side panel of its full properties + rendered annotations. This is the
  "props visualized properly" view GraphML/Gephi doesn't paint on the canvas.

Both share one text-rendering layer (`GraphText`).

**Approach (settled).** Emit GraphML by hand with `System.Xml.XmlWriter` from a new pure module in the
`BioFSharp.INSDC.ArcIR` library — no new NuGet dependency (netstandard2.0 ships `System.Xml`).
QuikGraph.Serialization was weighed and rejected: its writer reflects over *fixed* CLR properties, which
doesn't fit the dynamic `Properties: Map<Iri, ArcValue>` bag, and it would add a dependency to a packable
library for ~40 lines of `XmlWriter`. Worth revisiting only if we later want graph *algorithms*.

## Design

### API (`GraphMl.fs`, `module GraphMl` in `namespace Arc.Build`)

- `GraphMl.toString : ArcIR -> string`
- `GraphMl.writeFile : string -> ArcIR -> unit`  (path first, so `ir` pipes in)

Both go through a private `writeGraph : TextWriter -> ArcIR -> unit`. No changes to the core model,
converters, or `Mapping`.

### Encoding (ArcIR → GraphML)

Two passes: (1) scan objects/relations for the `<key>` schema and dangling endpoints; (2) write the
document with `edgedefault="directed"`.

Node `<key>`s (`for="node"`): `label` (title/name property else `Id.Value`), `kind` (`ArcObjectKind`
case name — the partition/color facet), `dtypes` (local names joined), one **`p_*` key per distinct
`Properties` IRI** (attr.name = the short convenience local name), and one **`a_*` key per distinct
annotation `Property` term** (attr.name = term `Name`, fallback IRI local name; so the ontology overlay
is *rendered as columns, not counted*). Edge `<key>`s: `predicate` (the edge label) plus one key per
distinct edge-`Properties` IRI.

Value rendering: `ArcValue` → string (`Integer`/`Float`/`Boolean` invariant, `DateTime` ISO-8601, `Iri`
/`Ref` as their string, `List` joined). `AnnotationValue` → string (`Literal`→value; `Term`→
`name (iri)`; `*WithUnit`→`value unit`). `Evidence`/`Source` provenance pointers are out of v1 (candidate
to emit as edges later).

Dangling endpoints: a relation may reference an `ArcId` absent from `Objects` (`Mapping` allows triples
to dangle). GraphML requires declared endpoints, so each missing id gets a **placeholder node**
(`kind=Missing`) — the edge stays valid and the unresolved reference is visible.

No tool-specific styling is baked in: Gephi colors nodes by partitioning on the `kind` column.

## Phased plan & status

1. [x] `plans/arcir-graphml.md` (this doc).
2. [x] `src/BioFSharp.INSDC.ArcIR/GraphMl.fs` + `<Compile>` entry after `Vocabulary.fs`.
3. [x] `GraphMlExportTests` (6 facts) in `tests/BioFSharp.INSDC.Tests/Tests.fs`: well-formed XML;
   node/edge counts incl. placeholders; a known node's `kind` + typed property column; an annotation
   rendered as a column; a known edge's `predicate`; a dangling target yields a placeholder node + valid
   edge.
4. [x] `playground/arcir_graphml.fsx` smoke/demo — writes `arcir.graphml` (17 nodes, 26 edges) + the
   interactive `arcir.html` from the 8 fixtures.
5. [x] `GraphText.fs` (shared) + `Html.fs` interactive viewer; `HtmlExportTests` (4 facts: self-contained
   document with no external resources; props + annotations embedded; `Missing` placeholders; a value
   containing `</script>` cannot break out of the script block).
6. [x] `./build.sh runtests` green — **73/73**.

## Build & verify

```sh
dotnet build src/BioFSharp.INSDC.ArcIR          # compiles GraphMl.fs, no new deps
./build.sh   runtests                           # full solution build + xUnit (GraphMlExportTests)
dotnet fsi   playground/arcir_graphml.fsx       # writes playground/arcir.graphml from 8 fixtures
```

Then open `playground/arcir.graphml` in Gephi: File ▸ Open (directed) → ForceAtlas 2 → Appearance ▸
Nodes ▸ Partition ▸ `kind` → set label to the `label` column → inspect properties/annotations in the
Data Laboratory.

## Key files

- Serializers: `src/BioFSharp.INSDC.ArcIR/{GraphMl,Html}.fs`, sharing `GraphText.fs`
- Core model consumed: `src/BioFSharp.INSDC.ArcIR/{ArcCore,ArcIR}.fs`
- Tests: `tests/BioFSharp.INSDC.Tests/Tests.fs` (`GraphMlExportTests`, `HtmlExportTests`)
- Smoke/demo: `playground/arcir_graphml.fsx` (writes both `arcir.graphml` and `arcir.html`)
