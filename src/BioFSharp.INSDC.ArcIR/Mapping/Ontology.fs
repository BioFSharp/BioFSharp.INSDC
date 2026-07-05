namespace BioFSharp.INSDC.ArcIR

open OBO.NET
open Arc.Build
open BioFSharp.IO.INSDC

/// Bridges the structural-ontology decompilation (`<Entity>.decompile` -> `DecompiledTerm list`) into
/// ArcIR annotations. This is the "semantics" half of the mapping: the typed converters build the object
/// graph, and this overlay attaches an ontology-backed, term-per-leaf view on top — reusing the ontology
/// rather than re-deriving field semantics by hand.
[<RequireQualifiedAccess>]
module Ontology =

    /// Ontology source recorded on the terms produced here.
    let [<Literal>] Source = "INSDC structural ontology"

    /// An OBO.NET term as an ArcIR `OntologyTerm` (id = term id, name carried, source tagged).
    let toOntologyTerm (term: OboTerm) : OntologyTerm =
        {
            Id = Iri.Create term.Id
            Name = Some term.Name
            Source = Some Source
        }

    /// One decompiled leaf as an `ArcAnnotation`: its ontology term is the annotation `Property` and the
    /// leaf value is a string `Literal`.
    // TODO: stamp the leaf's concrete XPath as provenance by attaching a `Selector` object and referencing
    // it via `Source` — deferred until the Selector-object convention is settled.
    let annotationOfLeaf (leaf: DecompiledTerm) : ArcAnnotation =
        ArcAnnotation.literal (toOntologyTerm leaf.Term) (ArcValue.String leaf.Value)

    /// Every decompiled leaf as annotations — the ontology overlay for one mapped object.
    let annotationsOfLeaves (leaves: DecompiledTerm list) : ArcAnnotation list =
        leaves |> List.map annotationOfLeaf

    /// True for a structural-ontology leaf that is one field of an INSDC `<Attribute>` (tag/value/units).
    /// Arbitrary attribute key/value metadata is lifted to first-class, *paired* annotations by the
    /// converters (`INSDC.attributeAnnotations`), so these flat, unpaired structural leaves are dropped
    /// here to avoid a redundant second copy (the "tag and value in a single list each" problem).
    let private isAttributeLeaf (leaf: DecompiledTerm) : bool =
        match leaf.Term.Name with
        | null -> false
        | name -> name.EndsWith ".Attribute.Tag" || name.EndsWith ".Attribute.Value" || name.EndsWith ".Attribute.Units"

    /// Decompile a parsed INSDC record (any entity — `decompile` is generic) and turn every
    /// structural-ontology leaf into an annotation, except the `<Attribute>` leaves (see above). This is
    /// the overlay a converter attaches to the entity's mapped object, reusing the ontology instead of
    /// hand-mapping field semantics.
    let annotationsOf (root: 'Root) : ArcAnnotation list =
        StructuralOntology.decompile root
        |> List.filter (isAttributeLeaf >> not)
        |> annotationsOfLeaves
