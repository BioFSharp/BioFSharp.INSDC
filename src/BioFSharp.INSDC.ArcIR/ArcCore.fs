namespace Arc.Build

open System

[<Struct>]
type Iri =
    private
    | Iri of string

    member this.Value =
        let (Iri value) = this
        value

    override this.ToString() =
        this.Value

    static member Create(value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg (nameof value) "IRI must not be empty."
        Iri value

    static member op_Explicit(value: string) =
        Iri.Create value

    static member op_Implicit(iri: Iri) =
        iri.Value

[<Struct>]
type ArcId =
    private
    | ArcId of string

    member this.Value =
        let (ArcId value) = this
        value

    override this.ToString() = this.Value

    static member Create(value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg (nameof value) "ARC id must not be empty."
        ArcId value


/// The closed structural classification of an ArcObject. The finer-grained semantics ride on
/// `DTypes` (see [Vocabulary]); this is the coarse category. INSDC concept → kind mapping lives in the
/// mapping docs.
type ArcObjectKind =
    | Observable // an entity/material an activity observes or consumes (orig. ISA Entity)
    | Instrument // a device/instrument an activity is carried out with
    | Resource // data or an addressable resource (a file, a URL)
    | Activity // a process/activity (orig. ISA Process)
    | Agent // a person, institution, or software agent
    | Role // the role an agent plays in an activity
    | Recipe // a plan/protocol (orig. ISA Plan)
    | Collection // a grouping dataset (ISA Investigation / Study / Assay)
    | Selector // an addressing/provenance selector (e.g. an XPath into a source record)


type ArcValue =
    | String of string
    | Integer of int64
    | Float of float
    | Boolean of bool
    | DateTime of DateTimeOffset
    | Iri of Iri
    | Ref of ArcId
    | List of ArcValue list


type OntologyTerm =
    {
        Id: Iri
        Name: string option
        Source: string option
    }


type AnnotationValue =
    | Literal of ArcValue
    | Term of OntologyTerm
    | LiteralWithUnit of value: ArcValue * unit: OntologyTerm
    | TermWithUnit of value: OntologyTerm * unit: OntologyTerm


type ArcAnnotation =
    {
        Property: OntologyTerm
        Value: AnnotationValue
        Evidence: ArcId option
        Source: ArcId option
    }


type ArcObject =
    {
        Id: ArcId
        Kind: ArcObjectKind
        DTypes: Set<Iri>
        Properties: Map<Iri, ArcValue>
        Annotations: ArcAnnotation list
    }


type ArcRelation =
    {
        Id: ArcId option
        Subject: ArcId
        Predicate: Iri
        Object: ArcId
        Properties: Map<Iri, ArcValue>
        Annotations: ArcAnnotation list
    }


type ArcIR =
    {
        Objects: Map<ArcId, ArcObject>
        Relations: Set<ArcRelation>
    }

    static member Empty =
        {
            Objects = Map.empty
            Relations = Set.empty
        }
