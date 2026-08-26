namespace BioFSharp.INSDC.ArcIR

open System
open System.Globalization
open BioFSharp.ArcIR

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

    let termLabel (ir: ArcIR) (termId: Iri) =
        let name =
            ir.Terms
            |> Map.tryFind termId
            |> Option.bind (fun term -> term.Name)
            |> Option.defaultValue (localName termId.Value)
        sprintf "%s (%s)" name termId.Value

    /// Render an annotation's value so graph diagnostics show the mapped value, not just a count.
    let renderAnnotationValue (ir: ArcIR) (av: AnnotationValue) =
        match av with
        | AnnotationValue.Literal v -> renderValue v
        | AnnotationValue.Term t -> termLabel ir t
        | AnnotationValue.LiteralWithUnit(v, u) -> sprintf "%s %s" (renderValue v) (termLabel ir u)
        | AnnotationValue.TermWithUnit(v, u) -> sprintf "%s %s" (termLabel ir v) (termLabel ir u)

    /// The display name for an annotation column: its term Name, else the term IRI's local name.
    let annotationName (ir: ArcIR) (a: ArcAnnotation) =
        ir.Terms
        |> Map.tryFind a.Property
        |> Option.bind (fun term -> term.Name)
        |> Option.defaultValue (localName a.Property.Value)

    /// A friendly node label. Accession is the stable identity, so it always wins over the (descriptive)
    /// title/name; falls back to the node's id (which is itself the accession/alias for entity nodes).
    let nodeLabel (o: ArcObject) =
        let pick (name: string) =
            o.Properties.Values
            |> Seq.tryPick (fun property ->
                if String.Equals(name, localName property.Predicate.Value, StringComparison.OrdinalIgnoreCase) then
                    Some(renderValue property.Value)
                else
                    None)
        pick "Accession"
        |> Option.orElseWith (fun () -> pick "Title")
        |> Option.orElseWith (fun () -> pick "Name")
        |> Option.defaultValue o.Id.Value
