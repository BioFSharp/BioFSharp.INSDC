namespace Arc.Build

open System
open System.Globalization

/// Shared text rendering for the ArcIR graph serializers (`GraphMl`, `Html`): IRI local names, kind and
/// value formatting, annotation display, node labels. Internal — not part of the package's public API.
module internal GraphText =

    /// The last path/fragment segment of an IRI (after the final '#' or '/'); the whole string otherwise.
    let localName (value: string) =
        let cut = max (value.LastIndexOf '#') (value.LastIndexOf '/')
        if cut >= 0 && cut + 1 < value.Length then value.Substring(cut + 1) else value

    let kindName kind =
        match kind with
        | ArcObjectKind.Observable -> "Observable"
        | ArcObjectKind.Instrument -> "Instrument"
        | ArcObjectKind.Resource -> "Resource"
        | ArcObjectKind.Activity -> "Activity"
        | ArcObjectKind.Agent -> "Agent"
        | ArcObjectKind.Role -> "Role"
        | ArcObjectKind.Recipe -> "Recipe"
        | ArcObjectKind.Collection -> "Collection"
        | ArcObjectKind.Selector -> "Selector"

    let rec renderValue (value: ArcValue) =
        match value with
        | ArcValue.String s -> s
        | ArcValue.Integer i -> string i
        | ArcValue.Float f -> f.ToString(CultureInfo.InvariantCulture)
        | ArcValue.Boolean b -> if b then "true" else "false"
        | ArcValue.DateTime d -> d.ToString("o", CultureInfo.InvariantCulture)
        | ArcValue.Iri iri -> iri.Value
        | ArcValue.Ref id -> id.Value
        | ArcValue.List xs -> xs |> List.map renderValue |> String.concat "; "

    let termLabel (term: OntologyTerm) =
        let name = term.Name |> Option.defaultValue (localName term.Id.Value)
        sprintf "%s (%s)" name term.Id.Value

    /// Render an annotation's value so the ontology overlay is *shown*, not just counted.
    let renderAnnotationValue (av: AnnotationValue) =
        match av with
        | AnnotationValue.Literal v -> renderValue v
        | AnnotationValue.Term t -> termLabel t
        | AnnotationValue.LiteralWithUnit(v, u) -> sprintf "%s %s" (renderValue v) (termLabel u)
        | AnnotationValue.TermWithUnit(v, u) -> sprintf "%s %s" (termLabel v) (termLabel u)

    /// The display name for an annotation column: its term Name, else the term IRI's local name.
    let annotationName (a: ArcAnnotation) =
        a.Property.Name |> Option.defaultValue (localName a.Property.Id.Value)

    /// A friendly node label. Accession is the stable identity, so it always wins over the (descriptive)
    /// title/name; falls back to the node's id (which is itself the accession/alias for entity nodes).
    let nodeLabel (o: ArcObject) =
        let pick (name: string) =
            o.Properties
            |> Seq.tryPick (fun kv ->
                if String.Equals(name, localName kv.Key.Value, StringComparison.OrdinalIgnoreCase) then
                    Some(renderValue kv.Value)
                else
                    None)
        pick "Accession"
        |> Option.orElseWith (fun () -> pick "Title")
        |> Option.orElseWith (fun () -> pick "Name")
        |> Option.defaultValue o.Id.Value
