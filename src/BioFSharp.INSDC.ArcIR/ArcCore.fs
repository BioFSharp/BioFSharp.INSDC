namespace Arc.Build

open System

/// A non-empty internationalized resource identifier used by the current
/// proof-of-concept graph for predicates, types, and property keys.
[<Struct>]
type Iri =
    private
    | Iri of string

    /// The identifier text.
    member this.Value =
        let (Iri value) = this
        value

    /// Returns the identifier text.
    override this.ToString() =
        this.Value

    /// Creates an IRI and rejects null, empty, or whitespace-only text.
    static member Create(value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg (nameof value) "IRI must not be empty."
        Iri value

    /// Explicitly converts non-empty text to an IRI.
    static member op_Explicit(value: string) =
        Iri.Create value

    /// Converts an IRI to its underlying text.
    static member op_Implicit(iri: Iri) =
        iri.Value

/// A non-empty object identifier in the current proof-of-concept graph.
[<Struct>]
type ArcId =
    private
    | ArcId of string

    /// The identifier text.
    member this.Value =
        let (ArcId value) = this
        value

    /// Returns the identifier text.
    override this.ToString() = this.Value

    /// Creates an identifier and rejects null, empty, or whitespace-only text.
    static member Create(value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg (nameof value) "ARC id must not be empty."
        ArcId value


/// The closed structural classification of an ArcObject. The finer-grained semantics ride on
/// `DTypes` (see [Vocabulary]); this is the coarse category. INSDC concept → kind mapping lives in the
/// mapping docs.
type ArcObjectKind =
    /// An entity or material observed or consumed by an activity.
    | Observable
    /// A device used by an activity.
    | Instrument
    /// Data or another addressable resource such as a file or URL.
    | Resource
    /// A process or activity.
    | Activity
    /// A person, institution, or software agent.
    | Agent
    /// A role played by an agent.
    | Role
    /// A plan or protocol.
    | Recipe
    /// A grouping dataset or generic object container.
    | Collection
    /// An addressing or provenance selector, such as an XPath.
    | Selector


/// A typed value carried by a graph property or annotation.
type ArcValue =
    /// Text.
    | String of string
    /// A signed 64-bit integer.
    | Integer of int64
    /// A double-precision floating-point number.
    | Float of float
    /// A Boolean.
    | Boolean of bool
    /// A timestamp with an offset.
    | DateTime of DateTimeOffset
    /// An IRI-valued term.
    | Iri of Iri
    /// A reference to another graph object.
    | Ref of ArcId
    /// An ordered value treated atomically by the current model.
    | List of ArcValue list


/// A term used as an annotation property, value, or unit.
type OntologyTerm =
    {
        /// The term identifier.
        Id: Iri
        /// An optional human-readable term name.
        Name: string option
        /// An optional vocabulary or mapping source label.
        Source: string option
    }


/// The value of an annotation, optionally expressed as a term or with a unit.
type AnnotationValue =
    /// A literal typed graph value.
    | Literal of ArcValue
    /// An ontology term value.
    | Term of OntologyTerm
    /// A literal value paired with an ontology unit.
    | LiteralWithUnit of value: ArcValue * unit: OntologyTerm
    /// An ontology term value paired with an ontology unit.
    | TermWithUnit of value: OntologyTerm * unit: OntologyTerm


/// A property/value assertion attached to an object or relation in the current
/// proof-of-concept model.
type ArcAnnotation =
    {
        /// The annotation predicate.
        Property: OntologyTerm
        /// The asserted value.
        Value: AnnotationValue
        /// An optional reference to supporting evidence.
        Evidence: ArcId option
        /// An optional reference to the source artifact.
        Source: ArcId option
    }


/// A typed node in the current proof-of-concept property graph.
type ArcObject =
    {
        /// The object identity.
        Id: ArcId
        /// The object's coarse structural category.
        Kind: ArcObjectKind
        /// Open semantic type IRIs.
        DTypes: Set<Iri>
        /// Explicit keyed values produced by converters.
        Properties: Map<Iri, ArcValue>
        /// Explicit annotation assertions produced by converters.
        Annotations: ArcAnnotation list
    }


/// A labeled directed edge in the current proof-of-concept property graph.
type ArcRelation =
    {
        /// An optional edge identity.
        Id: ArcId option
        /// The source object identifier.
        Subject: ArcId
        /// The relation predicate.
        Predicate: Iri
        /// The target object identifier.
        Object: ArcId
        /// Explicit values attached to the edge.
        Properties: Map<Iri, ArcValue>
        /// Explicit annotation assertions attached to the edge.
        Annotations: ArcAnnotation list
    }


/// The current in-memory proof-of-concept ArcIR property graph. The repository
/// roadmap will replace this shape with the target-neutral persistent core.
type ArcIR =
    {
        /// Objects keyed by stable graph identifier.
        Objects: Map<ArcId, ArcObject>
        /// Distinct labeled relations between objects.
        Relations: Set<ArcRelation>
    }

    /// An empty graph.
    static member Empty =
        {
            Objects = Map.empty
            Relations = Set.empty
        }
