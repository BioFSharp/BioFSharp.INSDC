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


type ArcObjectKind = 
    // Activity Inputs und Outputs, muss man sich namen überlegen, sollten semantisch kllar getrennt sein
    | Observable // Input für activity? orig. Entity
    | Instrument // ? Irgendwas mit dem man activity ausführt, soll nicht instrument heißen
    | Resource // evtl data, orig. Resource weil auch URL etc.


    | Activity // Process 
    | Agent // Person + AI Agents + Institution
    | Role // Role von Agent in Activity
    | Recipe // orig. Plan
    | Collection // praktisch generisches dataset, mappt auch I / S / A etc. -> Group?

    | Selector // evtl mergebar mit Resource


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