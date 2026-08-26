namespace BioFSharp.ArcIR

open System

/// A validated absolute internationalized resource identifier.
[<Struct>]
type Iri =
    private
    | Iri of string

    /// The identifier text exactly as supplied at construction time.
    member this.Value =
        let (Iri value) = this
        value

    /// Returns the identifier text.
    override this.ToString() = this.Value

    /// Attempts to create an absolute IRI.
    static member TryCreate(value: string) =
        if String.IsNullOrWhiteSpace value then
            None
        else
            match Uri.TryCreate(value, UriKind.Absolute) with
            | true, uri when not (String.IsNullOrWhiteSpace uri.Scheme) -> Some(Iri value)
            | _ -> None

    /// Creates an absolute IRI or raises `ArgumentException` for invalid input.
    static member Create(value: string) =
        match Iri.TryCreate value with
        | Some iri -> iri
        | None -> invalidArg (nameof value) "IRI must be a valid absolute identifier."

    /// Explicitly converts absolute identifier text to an IRI.
    static member op_Explicit(value: string) = Iri.Create value

    /// Converts an IRI to its underlying text.
    static member op_Implicit(iri: Iri) = iri.Value

/// The coarse structural classification of an ArcIR object.
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
    /// An addressing or provenance selector.
    | Selector

/// A typed value carried by a graph assertion or annotation.
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
    /// An IRI-valued ontology term.
    | Iri of Iri
    /// A reference to another graph object.
    | Ref of Iri
    /// An ordered value deliberately curated as one atomic assertion value.
    | List of ArcValue list

/// Reusable descriptive metadata for a term whose authoritative identifier is its map key.
type OntologyTerm =
    {
        /// An optional human-readable term name.
        Name: string option
        /// An optional vocabulary or mapping source label.
        Source: string option
    }

/// The value of an annotation, with term and unit values represented by registry references.
type AnnotationValue =
    /// A literal typed graph value.
    | Literal of ArcValue
    /// An ontology term value.
    | Term of Iri
    /// A literal value paired with an ontology unit.
    | LiteralWithUnit of value: ArcValue * unit: Iri
    /// An ontology term value paired with an ontology unit.
    | TermWithUnit of value: Iri * unit: Iri

/// An independently addressable annotation assertion.
type ArcAnnotation =
    {
        /// The annotation identity.
        Id: Iri
        /// The annotation-property term identifier.
        Property: Iri
        /// The asserted value.
        Value: AnnotationValue
        /// An optional supporting graph object.
        Evidence: Iri option
        /// An optional source graph object.
        Source: Iri option
    }

/// An independently addressable type assertion.
type ArcTypeAssertion =
    {
        /// The assertion identity.
        Id: Iri
        /// The asserted ontology-term identifier.
        Term: Iri
    }

/// An independently addressable property assertion.
type ArcProperty =
    {
        /// The assertion identity.
        Id: Iri
        /// The predicate term identifier.
        Predicate: Iri
        /// The asserted value.
        Value: ArcValue
        /// Annotations keyed by their authoritative identities.
        Annotations: Map<Iri, ArcAnnotation>
    }

/// A typed node in the ArcIR property graph.
type ArcObject =
    {
        /// The object identity.
        Id: Iri
        /// The object's coarse structural category.
        Kind: ArcObjectKind
        /// Type assertions keyed by their authoritative identities.
        Types: Map<Iri, ArcTypeAssertion>
        /// Property assertions keyed by their authoritative identities.
        Properties: Map<Iri, ArcProperty>
        /// Object annotations keyed by their authoritative identities.
        Annotations: Map<Iri, ArcAnnotation>
    }

/// An independently addressable directed relation.
type ArcRelation =
    {
        /// The relation identity.
        Id: Iri
        /// The source object identifier.
        Subject: Iri
        /// The relation-predicate term identifier.
        Predicate: Iri
        /// The target object identifier.
        Object: Iri
        /// Property assertions keyed by their authoritative identities.
        Properties: Map<Iri, ArcProperty>
        /// Relation annotations keyed by their authoritative identities.
        Annotations: Map<Iri, ArcAnnotation>
    }

/// The target-neutral in-memory ArcIR property graph.
type ArcIR =
    {
        /// Term definitions keyed by their authoritative identifiers.
        Terms: Map<Iri, OntologyTerm>
        /// Objects keyed by their authoritative identifiers.
        Objects: Map<Iri, ArcObject>
        /// Relations keyed by their authoritative identifiers.
        Relations: Map<Iri, ArcRelation>
    }

    /// An empty graph.
    static member Empty =
        { Terms = Map.empty
          Objects = Map.empty
          Relations = Map.empty }

/// Construction helpers for reusable term definitions.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module OntologyTerm =

    /// Creates a term definition.
    let create name source = { Name = name; Source = source }

module private Construction =

    let keyed argumentName (values: 'value seq) (idOf: 'value -> Iri) =
        ((Map.empty, Set.empty), values)
        ||> Seq.fold (fun (result, seen) value ->
            let id = idOf value
            if Set.contains id seen then
                invalidArg argumentName (sprintf "Duplicate assertion identity: %s" id.Value)
            Map.add id value result, Set.add id seen)
        |> fst

/// Construction helpers for normalized graph assertions.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ArcAnnotation =

    /// Creates an annotation assertion.
    let create id property value evidence source =
        { Id = id
          Property = property
          Value = value
          Evidence = evidence
          Source = source }

/// Construction helpers for normalized type assertions.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ArcTypeAssertion =

    /// Creates a type assertion.
    let create id term = { Id = id; Term = term }

/// Construction helpers for normalized property assertions.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ArcProperty =

    /// Creates a property assertion.
    let create id predicate value (annotations: ArcAnnotation seq) =
        { Id = id
          Predicate = predicate
          Value = value
          Annotations = Construction.keyed (nameof annotations) annotations (fun annotation -> annotation.Id) }

/// Construction helpers for graph objects.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ArcObject =

    /// Creates an object from already-addressed assertions.
    let create id kind (types: ArcTypeAssertion seq) (properties: ArcProperty seq) (annotations: ArcAnnotation seq) =
        { Id = id
          Kind = kind
          Types = Construction.keyed (nameof types) types (fun assertion -> assertion.Id)
          Properties = Construction.keyed (nameof properties) properties (fun assertion -> assertion.Id)
          Annotations = Construction.keyed (nameof annotations) annotations (fun annotation -> annotation.Id) }

/// Construction helpers for graph relations.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ArcRelation =

    /// Creates an addressed relation.
    let create id subject predicate objectId (properties: ArcProperty seq) (annotations: ArcAnnotation seq) =
        { Id = id
          Subject = subject
          Predicate = predicate
          Object = objectId
          Properties = Construction.keyed (nameof properties) properties (fun assertion -> assertion.Id)
          Annotations = Construction.keyed (nameof annotations) annotations (fun annotation -> annotation.Id) }
