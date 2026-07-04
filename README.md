# BioFSharp.INSDC

![Logo](docs/img/Logo_large.png)

Read, write, model, store, and visualize [INSDC](https://www.insdc.org/) sequence-database records —
BioProject, Study, Sample, Experiment, Run, Analysis, Submission, Receipt — as a direct dependency of
[BioFSharp](https://github.com/CSBiology/BioFSharp).

The repo is a layered pipeline: a **C# type model** generated from the official ENA/SRA XSDs, an **F# IO
layer** that reads/writes those records, an **ArcIR mapping** that lifts a parsed record into an
ARC-oriented **property graph** of typed, related objects, and a **SQLite store**. A distinctive feature
is that every field's *location* (XPath) and *meaning* (an ontology term) are both derived by reflection
from the one type model, so they can never drift from it — and that ontology is what supplies ArcIR's
semantic annotation layer.

## Packages

| Package | Target | Purpose |
| --- | --- | --- |
| `BioFSharp.FileFormats.INSDC` | netstandard2.0 | C# type model auto-generated from the [ENA SRA XSDs](https://ftp.ebi.ac.uk/pub/databases/ena/doc/xsd/sra_1_5/) via [`dotnet-xscgen`](https://www.nuget.org/packages/dotnet-xscgen). Also carries the generated `FragmentSelectors` maps. |
| `BioFSharp.IO.INSDC` | netstandard2.0 | F# read/write per entity, plus per-instance XPath tracking and the structural-ontology **decompilation**. Embeds `StructuralOntology.obo`. |
| `BioFSharp.INSDC.ArcIR` | netstandard2.0 | Maps parsed records into **ArcIR** — a property graph of typed objects and relations — and serializes it to GraphML and interactive HTML. |
| `BioFSharp.INSDC.SQLite` | netstandard2.0 | SQLite-backed store: deconstructs entities into a normalized schema and reconstructs them on read. |

The C# split exists because there is no F# equivalent of `XmlSchemaClassGenerator`.

## The build chain

Everything hangs off one generated type model. Two reflection-derived artifacts sit on top of it — one
saying **where** each value lives (`FragmentSelectors`), one saying **what** each value is
(`StructuralOntology.obo`) — and the runtime `decompile` step joins them against a parsed record. ArcIR
then consumes both the typed record (for structure) and the decompilation (for semantics).

```text
 ENA SRA XSDs
    │  dotnet xscgen                          (./build.sh regenerateInsdcTypes)
    ▼
 BioFSharp.FileFormats.INSDC  ── C# typed record model ─────────────────┐  reflection over the model's
    │                                                                    │  System.Xml.Serialization attrs
    │  read / readString / write  (BioFSharp.IO.INSDC)                   │
    ▼                                                                    ├─► FragmentSelectors.cs
 parsed record                                                          │      WHERE: dotted path → #xpointer(xpath)
    │                                                                    │      (./build.sh generateFragmentSelectors)
    │  xpathEntries  ──► per-leaf  { XPath (positional), Value }         │
    │                        │                                          └─► StructuralOntology.obo
    │                        │  join: strip [n] positions,                     WHAT: structural xpath → ontology term
    │                        │  look structural xpath up in the ontology       (./build.sh generateStructuralOntology,
    │                        ▼                                                   built FROM FragmentSelectors)
    │                 decompile ──► DecompiledTerm list { Term; XPath; Value }
    │                        │
    │  typed converters      │  Ontology.annotationsOf
    │  (STRUCTURE)           │  (SEMANTICS: one ArcAnnotation per leaf)
    ▼                        ▼
 ArcIR property graph  ◄──── annotation overlay
    │
    ├─ GraphMl.writeFile → .graphml   (Gephi / yEd / Cytoscape desktop)
    └─ Html.writeFile    → .html      (self-contained interactive viewer)
```

### 1. Type model — `BioFSharp.FileFormats.INSDC`

`dotnet xscgen` turns the committed ENA/SRA XSDs (`src/BioFSharp.FileFormats.INSDC/schemas/`) into a C#
record model under `Generated/`. This is regenerated on demand (`regenerateInsdcTypes`), never hand-edited.
See [Generated type naming](#generated-type-naming) for how the mechanical type names are cleaned up.

### 2. IO — `BioFSharp.IO.INSDC`

Each entity is an F# module exposing:

- `read : string -> seq<'Entity>` / `readString : string -> seq<'Entity>` — parse a file/string
  (`Receipt.read` returns a single record).
- `write` / `writeString` — serialize back to INSDC XML.
- `xpathOf` / `xpointerOf` — the concrete, position-qualified XPath (or W3C `#xpointer(...)`) of a given
  property on a parsed value, selected with a quotation (`<@ fun b -> b.Accession @>`).
- `xpathEntries : 'Entity -> XPathEntry[]` — **every** present leaf of a parsed record as
  `{ Path; XPath; Value }` (property path, positional XPath, string value).
- `decompile : 'Entity -> DecompiledTerm list` — the structural-ontology view (below).

The 8 IO-readable entities are BioProject, Study, BioSample, Experiment, Run, Analysis, Submission, and
Receipt.

### 3. Two generated companions — `where` and `what`

Both are produced by FAKE targets that reflect over the built type model; both are on-demand only (the
default build does not depend on them) and are committed.

**`FragmentSelectors` (where).** [`build/FragmentSelectorTasks.fs`](build/FragmentSelectorTasks.fs) emits,
for each entity, a `partial class` exposing
`FragmentSelectors : IReadOnlyDictionary<string,string>` mapping a dotted property path to its XPointer
fragment selector — derived from the same `System.Xml.Serialization` attributes the serializer uses, so
it cannot drift from the model:

```text
BioProject.FragmentSelectors["Accession"] = "#xpointer(/PROJECT/@accession)"
```

**`StructuralOntology.obo` (what).** [`build/StructuralOntologyTasks.fs`](build/StructuralOntologyTasks.fs)
builds an OBO ontology *from the FragmentSelectors maps*. For every leaf `(dottedPath -> #xpointer(xpath))`
it synthesizes a chain of container terms (one per dotted prefix, plus an entity root) wired with
`part_of`, and a leaf term that carries the bare structural XPath as a `property_value`:

```text
[Term]
id: INSDC:0000123
name: BioProject.Name
def: "INSDC BioProject field Name at /PROJECT/NAME"
relationship: part_of INSDC:0000001        ← container chain mirrors the XML
property_value: insdc_xpath /PROJECT/NAME   ← the join key decompile uses
```

The ontology deliberately mirrors the *XML* element structure (splicing back the wrapped-collection item
levels xscgen collapses), not the flatter property graph. Names are labels only; the `insdc_xpath` join
key is what matters at runtime.

### 4. Decompilation — the join

[`StructuralOntology.decompile`](src/BioFSharp.IO.INSDC/Internal/StructuralOntology.fs) is where *where*
and *what* meet. It parses the embedded `StructuralOntology.obo` once (via OBO.NET) into an index
`structural xpath -> OboTerm`, then for each per-leaf `XPathEntry` of a parsed record it strips the array
positions (`COLLABORATOR[2]` → `COLLABORATOR`) and looks the structural XPath up:

```fsharp
let decompile (root: 'Root) : DecompiledTerm list =
    XPathTracking.xpathEntries root
    |> Array.choose (fun e ->
        tryTermForXPath e.XPath                      // structural xpath -> ontology term
        |> Option.map (fun t -> { Term = t; XPath = e.XPath; Value = e.Value }))
    |> List.ofArray
```

So a `DecompiledTerm` is a parsed leaf tagged with *what it is*: the ontology term (`Term`), the concrete
positional XPath it came from (`XPath`), and its string `Value`.

### 5. How the decompilation wires into the ArcIR graph

ArcIR uses a **hybrid** mapping (see [`plans/arcir-mapping.md`](plans/arcir-mapping.md)):

- **Structure** — objects, typed values, sub-objects, and cross-entity edges — comes from **per-entity
  typed converters** (`Mapping/INSDC.fs`) that read the parsed record directly.
- **Semantics** — a per-leaf, ontology-termed annotation layer — comes from the **decompilation overlay**,
  reused rather than re-deriving field meaning by hand.

The bridge is [`Mapping/Ontology.fs`](src/BioFSharp.INSDC.ArcIR/Mapping/Ontology.fs). It runs `decompile`
on the record and turns every leaf into an `ArcAnnotation` whose `Property` is the ontology term and whose
`Value` is a string `Literal`:

```fsharp
// One decompiled leaf -> one ArcAnnotation (ontology term as the property, value as a literal).
let annotationOfLeaf (leaf: DecompiledTerm) : ArcAnnotation =
    ArcAnnotation.literal (toOntologyTerm leaf.Term) (ArcValue.String leaf.Value)

// Decompile a record and turn every structural-ontology leaf into annotations.
let annotationsOf (root: 'Root) : ArcAnnotation list =
    StructuralOntology.decompile root |> List.map annotationOfLeaf
```

Each converter attaches `Ontology.annotationsOf record` to the entity's `ArcObject.Annotations`. The net
effect: a mapped node carries the typed, structural properties the converter set *plus* an ontology-backed
annotation for **every** leaf of the source record — e.g. a Submission node ends up with its structural
properties and ~14 annotations such as `Submission.Title → "Submitted by NIG on 28-JAN-2017"`, each keyed
by a term from the generated structural ontology. (Attaching each annotation's XPath as an explicit
`Selector` provenance node is a documented TODO.)

## ArcIR — the property graph

`BioFSharp.INSDC.ArcIR` maps parsed records into **ArcIR**, an ARC-oriented intermediate representation
shaped as an RDF/property graph. The core model ([`ArcCore.fs`](src/BioFSharp.INSDC.ArcIR/ArcCore.fs)):

```fsharp
type ArcIR =
    { Objects: Map<ArcId, ArcObject>       // nodes, keyed by id
      Relations: Set<ArcRelation> }        // directed, labeled edges

type ArcObject =
    { Id: ArcId
      Kind: ArcObjectKind                  // closed 9-case structural class
      DTypes: Set<Iri>                      // open, IRI-typed semantic tags
      Properties: Map<Iri, ArcValue>        // typed key/value bag (structure)
      Annotations: ArcAnnotation list }     // ontology overlay (semantics, from decompile)

type ArcRelation =
    { Id: ArcId option
      Subject: ArcId; Predicate: Iri; Object: ArcId   // subject --predicate--> object
      Properties: Map<Iri, ArcValue>; Annotations: ArcAnnotation list }
```

It is a general **directed** graph — cyclic, multi-parent, and tolerant of dangling edge endpoints — not a
tree. Key ideas:

- **Node `Kind`** is one of `Collection`, `Observable`, `Activity`, `Instrument`, `Recipe`, `Agent`,
  `Role`, `Resource`, `Selector`; finer semantics ride on `DTypes`.
- **Controlled vocabulary** ([`Vocabulary.fs`](src/BioFSharp.INSDC.ArcIR/Vocabulary.fs)) mints predicate
  and DType IRIs under one base, so the whole graph speaks one vocabulary (`hasStudy`, `hasSample`,
  `usesInstrument`, `producesData`, `acknowledges`, …).
- **Sub-objects** (organisms/taxa, instruments, protocols, data files, agents) are first-class nodes with
  deterministic ids, so shared things collapse to one node via merge-on-id — a taxon `taxon:3702` or an
  institution `org:ddbj` is referenced by many entities instead of being duplicated.
- **Two-pass assembly.** Converters emit objects plus *pending* relations; `INSDC.build` folds all objects
  in (deduping) then resolves each pending relation against the full node set — a reference may dangle
  until the record it points at is loaded.

### Building a graph

`Mapping/INSDC.fs` has one converter per entity plus `INSDC.build`:

```fsharp
let ir =
    [ INSDC.bioProject project
      INSDC.study study
      INSDC.experiment experiment
      // … one per readable entity …
      INSDC.receipt receipt ]
    |> INSDC.build     // : ConversionResult seq -> ArcIR
```

Traversal helpers live in [`ArcIR.fs`](src/BioFSharp.INSDC.ArcIR/ArcIR.fs) (`outgoing`, `incoming`,
`objectsByKind`, `objectsByDType`).

### Visualizing a graph

Two serializers render an `ArcIR`, sharing one text-rendering layer (`GraphText.fs`):

- **GraphML** — `GraphMl.writeFile "arcir.graphml" ir` — for **Gephi** (preferred), yEd, or Cytoscape
  desktop. Nodes carry `label`/`kind`/`dtypes` plus one data column per distinct property IRI and per
  distinct annotation term; edges carry the `predicate` label; dangling endpoints become `Missing`
  placeholder nodes. In Gephi: layout with ForceAtlas 2, color by *Appearance ▸ Partition ▸ kind*, inspect
  properties/annotations in the Data Laboratory.
- **Interactive HTML** — `Html.writeFile "arcir.html" ir` — a single self-contained, offline page (embedded
  force-directed SVG, no external scripts/CDN) where nodes are colored by kind, edges labeled by predicate,
  and **clicking a node opens a side panel of its full properties and rendered annotations**.

Run both over the committed fixtures with:

```bash
dotnet build src/BioFSharp.INSDC.ArcIR
dotnet fsi   playground/arcir_graphml.fsx      # writes playground/arcir.graphml and arcir.html
```

See [`plans/arcir-graphml.md`](plans/arcir-graphml.md) for the serializer design.

## SQLite store — `BioFSharp.INSDC.SQLite`

A SQLite-backed store that deconstructs BioProject, Study, BioSample, Experiment, and Run values into a
normalized relational schema and reconstructs them on read (design: [`plans/sqlite-store.md`](plans/sqlite-store.md)).

## Build

First-time setup installs the pinned tools (`dotnet-xscgen`, `fsdocs`):

```bash
dotnet tool restore
```

Then, via the FAKE build (a thin `./build.sh <target>` shell over `build/`):

```bash
./build.sh                          # default: build the solution
./build.sh runtests                 # build + run the xUnit suite
./build.sh pack                     # produce NuGet packages
./build.sh builddocs                # build the fsdocs site

# On-demand code generation (not part of the default build; commit the output):
./build.sh regenerateInsdcTypes         # re-run xscgen after the XSDs change
./build.sh generateFragmentSelectors    # regenerate FragmentSelectors.cs from the model
./build.sh generateStructuralOntology   # regenerate StructuralOntology.obo from FragmentSelectors
```

CI (`.github/workflows/`) is a deliberately thin shell around these targets — it runs
`./build.sh runtests` on Linux and Windows.

## Generated type naming

`dotnet xscgen` derives C# type names mechanically from the XSDs, which produces verbose names like
`AnalysisTypeAnalysisTypeTranscriptomeAssembly`. We clean these up via
[`src/BioFSharp.FileFormats.INSDC/schemas/typename-substitutions.txt`](src/BioFSharp.FileFormats.INSDC/schemas/typename-substitutions.txt),
passed to the tool with `--tnsf`. The substitution file:

- Has one rule per line in the form `A:<xscgen-default-name>=<substitute>` (the `A:` prefix matches any
  type/member; lines starting with `#` are comments).
- Documents its naming rules (A–F) in a header block — read those before adding rules so renames stay
  consistent.
- Is the **only** place to change a generated type's name; never hand-edit files under `Generated/`.

To add or change a substitution:

1. Edit `typename-substitutions.txt`. The left side is the name xscgen would emit without any substitution
   (the original XSD-derived path); the right side is the desired C# identifier. Pick a substitute that does
   not collide with another type — xscgen falls back to a generic name (e.g. `<Name>Item`) if the substitute
   clashes with an existing default.
2. Run `./build.sh regenerateInsdcTypes`.
3. Commit both the updated substitution file and the regenerated files under `Generated/`.

Caveats:

- xscgen's substitution file does not accept regex or dotted/nested names — `Foo.Bar` would emit invalid
  C# (`class Foo.Bar`). Substitutes must be flat C# identifiers.
- Stay consistent with the rules already documented in the file's header. If a rename does not fit any
  existing rule, add a new lettered rule alongside the others.

## Repo layout

```text
.
├── build/                                  FAKE build project (targets + code generators)
├── docs/                                   fsdocs sources + images
├── plans/                                  design docs
│   ├── implementation.md                     base read/write roadmap
│   ├── xml-fragment-selectors.md             FragmentSelectors + XPath tracking
│   ├── arcir-mapping.md                      INSDC → ArcIR mapping
│   ├── arcir-graphml.md                      GraphML + HTML visualization
│   └── sqlite-store.md                       SQLite store
├── playground/                             .fsx smoke/demo scripts
├── src/
│   ├── BioFSharp.FileFormats.INSDC/        C# generated type model (+ schemas/, Generated/, FragmentSelectors.cs)
│   ├── BioFSharp.IO.INSDC/                 F# read/write, XPath tracking, decompilation (+ StructuralOntology.obo)
│   ├── BioFSharp.INSDC.ArcIR/              ArcIR mapping + GraphML/HTML serializers
│   └── BioFSharp.INSDC.SQLite/             SQLite store
└── tests/BioFSharp.INSDC.Tests/            xUnit tests, with committed ENA fixtures
```

## Contributing

See [`AGENTS.md`](AGENTS.md) for repo conventions and the design docs under [`plans/`](plans/) for the
implementation roadmaps.
