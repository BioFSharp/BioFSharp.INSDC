namespace BioFSharp.INSDC.ArcIR

open Arc.Build
open BioFSharp.FileFormats.INSDC

/// Shared folders that turn INSDC composite value-objects into ArcIR annotations (and, for identifiers,
/// cross-entity edges) — preserving object integrity by emitting one annotation per composite instead of
/// one per leaf. Synthetic term ids are minted under a documented base with positional disambiguation,
/// since several values can share a key. Consumed by the per-entity converters.
[<RequireQualifiedAccess>]
module Annotations =

    [<Literal>]
    let private baseIri = "http://purl.org/arc/insdc/"

    /// Sanitize a free-text key into an IRI-safe fragment (non-alphanumeric -> '_').
    let private slug (s: string) =
        System.String(s.Trim().ToCharArray() |> Array.map (fun c -> if System.Char.IsLetterOrDigit c then c else '_'))

    let private nz (s: string) = if isNull s then "" else s

    /// One literal annotation: term (id, Name, source) + a literal value.
    let private literal (termId: string) (name: string) (source: string) (value: ArcValue) : ArcAnnotation =
        ArcAnnotation.literal (ArcAnnotation.term termId (Some name) (Some source)) value

    // ---- scalar entity fields ----

    /// A single scalar field of an entity as an annotation (term Name = key; one field per key, so the id
    /// is key-derived). `source` records the entity provenance.
    let field (source: string) (key: string) (value: ArcValue) : ArcAnnotation =
        literal (sprintf "%sfield/%s" baseIri (slug key)) key source value

    /// A string field as an annotation, or `None` when the value is absent/blank.
    let stringField (source: string) (key: string) (value: string) : ArcAnnotation option =
        if System.String.IsNullOrWhiteSpace value then None else Some(field source key (ArcValue.String value))

    // ---- INSDC <Attribute> (tag / value / optional units) ----

    [<Literal>]
    let private attributeSource = "INSDC attribute"

    let private attributeBase = baseIri + "attribute/"

    /// INSDC `<Attribute>`s as annotations: tag -> term Name, value -> literal, optional units -> unit.
    let attributeAnnotations (attrs: seq<Attribute>) : ArcAnnotation list =
        attrs
        |> Seq.mapi (fun i a ->
            if System.String.IsNullOrWhiteSpace a.Tag then
                None
            else
                let property =
                    ArcAnnotation.term (sprintf "%s%s/%d" attributeBase (slug a.Tag) (i + 1)) (Some a.Tag) (Some attributeSource)

                let value = ArcValue.String(nz a.Value)

                match Option.ofObj a.Units with
                | Some units when not (System.String.IsNullOrWhiteSpace units) ->
                    let unit =
                        ArcAnnotation.term (sprintf "%sunit/%s" attributeBase (slug units)) (Some units) (Some attributeSource)

                    Some(ArcAnnotation.literalWithUnit property value unit)
                | _ -> Some(ArcAnnotation.literal property value))
        |> Seq.choose id
        |> List.ofSeq

    // ---- INSDC <IDENTIFIERS> ----

    [<Literal>]
    let private identifierSource = "INSDC identifier"

    let private identifierBase = baseIri + "identifier/"

    /// Namespaces (lower-cased) whose external identifiers denote an entity we model as a node, so a
    /// `references` edge can be drawn to the named record.
    let private modelledNamespaces =
        set [ "bioproject"; "study"; "biosample"; "sample"; "experiment"; "run"; "analysis"; "submission" ]

    /// Fold an INSDC `<IDENTIFIERS>` block into annotations plus optional cross-entity `references` edges,
    /// preserving object integrity. `Name`-typed ids (primary/secondary/uuid) keep the kind as the term;
    /// `QualifiedName`-typed ids (external/submitter) use the namespace as the term (value = the id). An
    /// external/submitter id whose namespace names a modelled entity (and isn't the subject itself) also
    /// yields a pending `references` edge, resolved once the whole graph is assembled.
    let identifierAnnotations (subjectId: string) (ids: Identifier) : ArcAnnotation list * PendingRelation list =
        if isNull ids then
            [], []
        else
            let anns = ResizeArray<ArcAnnotation>()
            let edges = ResizeArray<PendingRelation>()

            let addName (kind: string) (i: int) (n: Name) =
                if not (isNull n) && not (System.String.IsNullOrWhiteSpace n.Value) then
                    anns.Add(literal (sprintf "%s%s/%d" identifierBase kind (i + 1)) kind identifierSource (ArcValue.String n.Value))

            let addQualified (i: int) (q: QualifiedName) =
                if not (isNull q) && not (System.String.IsNullOrWhiteSpace q.Value) then
                    let ns =
                        if System.String.IsNullOrWhiteSpace q.Namespace then "externalId" else q.Namespace

                    anns.Add(literal (sprintf "%s%s/%d" identifierBase (slug ns) (i + 1)) ns identifierSource (ArcValue.String q.Value))

                    if modelledNamespaces.Contains(ns.Trim().ToLowerInvariant()) && q.Value <> subjectId then
                        edges.Add
                            { Subject = ArcId.Create subjectId
                              Predicate = Vocabulary.Rel.references
                              TargetAccession = Some q.Value
                              TargetRefname = None
                              TargetRefcenter = None }

            addName "primaryId" 0 ids.PrimaryId
            ids.SecondaryId |> Seq.iteri (addName "secondaryId")
            ids.Uuid |> Seq.iteri (addName "uuid")
            ids.ExternalId |> Seq.iteri addQualified
            addQualified 0 ids.SubmitterId

            List.ofSeq anns, List.ofSeq edges
