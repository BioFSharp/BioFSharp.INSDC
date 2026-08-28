# BioFSharp.ArcIR

`BioFSharp.ArcIR` is the target-neutral graph core used by BioFSharp metadata
adapters. It contains validated absolute IRI identities, a normalized property
graph, explicit lossless graph operations, structural validation, and
canonical, schema-versioned JSON persistence.

The package does not reference INSDC schemas, crawler or SQLite code, SSSOM, or
any output target. Source-specific ingestion belongs in adapter packages such as
`BioFSharp.INSDC.ArcIR`.

All independently curatable elements are keyed by stable `Iri` identities.
Ontology term definitions are shared through `ArcIR.Terms`; assertions reference
those definitions rather than embedding copies.

## Immutable JSON states

`ArcIRJson` reads and writes canonical `.arcir.json` state artifacts. Version 1.0
uses deterministic ordinal key ordering, invariant scalar encodings, UTF-8
without a byte-order mark, and a fixed LF layout. Collection keys are the
authoritative identities, so redundant `id` fields are not written. The bundled
`schema/arcir-1.0.schema.json` describes the complete wire format.

```fsharp
let revision = ArcIRJson.writeNew "arcir/states/state-id.arcir.json" graph

let location = ArcJsonLocation.PropertyValue(objectId, assertionId)
let fragment = revision |> Result.map (fun artifact -> ArcIRJson.fragmentRef artifact location)
```

`writeNew` publishes through a same-directory temporary file and never replaces
an existing state path. The returned `ArtifactRevision` binds that path to the
SHA-256 digest of the exact canonical bytes.

`ArcJsonLocation` covers terms, objects, assertions, annotations, relations, and
their atomic value occurrences. `ArcIRJson.selector` converts a location to an
RFC 6901 URI-fragment JSON Pointer; `resolveFragment` verifies the artifact digest
before resolving it. Scalar values do not gain model IDs, and lists remain one
atomic assertion value.

Artifact revisions and fragment selectors designate persisted occurrences. They
do not add history or provenance to `ArcIR`; downstream ARC integrations relate
these designated inputs and outputs through the ARC's native process model.

## Selected literal-to-term companions

`ArcIRJson.parseLocation` is the inverse of `ArcIRJson.selector` for every typed
ArcIR location. It validates RFC 6901 URI-fragment escaping and rejects pointers
to fields that are not represented by `ArcJsonLocation`.

`LiteralMapping.apply` adds a registered term as the deterministic semantic
companion of one exact string-literal occurrence while retaining the original
literal:

```fsharp
let source =
    match ArcIRJson.parseLocation selector with
    | Ok location -> location
    | Error errors -> failwithf "Invalid ArcIR selector: %A" errors

let result =
    LiteralMapping.apply
        { Source = source
          Literal = "control"
          Target = Iri.Create "http://purl.obolibrary.org/obo/OBI_0000220" }
        graph
```

Supported sources are object and relation string properties plus literal-valued
object, relation, and nested property annotations. Lists and non-string scalars
remain atomic and unsupported. A compatible replay returns `AlreadyPresent` and
the same output location; missing occurrences, changed literals, unregistered
targets, wrong value kinds, invalid graphs, and deterministic identity conflicts
return typed failures without a partially changed graph. SSSOM selection and ARC
process provenance remain application concerns and are not dependencies of this
core transformation.
