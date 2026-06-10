namespace BioFSharp.IO.INSDC.Internal

open System
open System.Collections
open System.Collections.Generic
open System.Reflection
open System.Text
open System.Xml.Serialization

open Microsoft.FSharp.Quotations

/// Resolves the concrete, position-qualified W3C XPointer of a property on a parsed INSDC value.
/// Public entity modules expose this as `<Entity>.xpathOf`. The property is named with a quotation
/// (`<@ fun b -> b.Name @>`) rather than piped by value, because a scalar value carries no
/// back-reference to its property, owner, or array position. The XPath is derived from the same
/// `System.Xml.Serialization` attributes the serializer uses, with array positions taken from the
/// indices written in the quotation and validated against the instance — so it points at exactly
/// the node `read` produced. This is the runtime, per-instance counterpart of the generated,
/// type-level `FragmentSelectors` maps in BioFSharp.FileFormats.INSDC.
module XPathTracking =

    /// The single attribute of type `'a` on a member, or None.
    let private tryAttr<'a when 'a :> Attribute> (m: MemberInfo) : 'a option =
        match Attribute.GetCustomAttribute(m, typeof<'a>) with
        | null -> None
        | a -> Some(a :?> 'a)

    let private hasAttr<'a when 'a :> Attribute> (m: MemberInfo) : bool = (tryAttr<'a> m).IsSome

    /// True for an element/array property whose value is a repeatable collection (not a string).
    let private isCollection (t: Type) : bool =
        t <> typeof<string>
        && (t.IsArray
            || t.GetInterfaces()
               |> Array.exists (fun i ->
                   i.IsGenericType && i.GetGenericTypeDefinition() = typedefof<IEnumerable<_>>))

    /// The relative XPath piece for a property and whether it is a repeatable collection, or None
    /// when the property is not serialized (`[XmlIgnore]` or unannotated).
    let private relativeStep (p: PropertyInfo) : (string * bool) option =
        let orElse fallback (s: string) = if String.IsNullOrEmpty s then fallback else s
        if hasAttr<XmlIgnoreAttribute> p then None
        else
            match tryAttr<XmlAttributeAttribute> p with
            | Some att -> Some("@" + (att.AttributeName |> orElse p.Name), false)
            | None ->
                match tryAttr<XmlArrayAttribute> p with
                | Some arr ->
                    let arrName = arr.ElementName |> orElse p.Name
                    let itemName =
                        match tryAttr<XmlArrayItemAttribute> p with
                        | Some item -> item.ElementName |> orElse arrName
                        | None -> arrName
                    Some(arrName + "/" + itemName, true)
                | None ->
                    match tryAttr<XmlElementAttribute> p with
                    | Some el -> Some(el.ElementName |> orElse p.Name, isCollection p.PropertyType)
                    | None -> if hasAttr<XmlTextAttribute> p then Some("text()", false) else None

    /// Append a 1-based positional predicate to the last element of a relative piece:
    /// `COLLABORATORS/COLLABORATOR`, index 1 -> `COLLABORATORS/COLLABORATOR[2]`.
    let private withIndex (piece: string) (index: int) : string =
        let slash = piece.LastIndexOf('/')
        if slash < 0 then sprintf "%s[%d]" piece (index + 1)
        else sprintf "%s%s[%d]" (piece.Substring(0, slash + 1)) (piece.Substring(slash + 1)) (index + 1)

    /// The XML root element name for a type carrying `[XmlRoot]`.
    let private rootElementName (t: Type) : string =
        match tryAttr<XmlRootAttribute> t with
        | Some r -> r.ElementName |> fun s -> if String.IsNullOrEmpty s then t.Name else s
        | None -> failwithf "Type '%s' has no [XmlRoot] and cannot anchor an absolute XPath." t.Name

    /// Evaluate a quotation index expression to an int (a literal, or a captured value).
    let private evalIndex (e: Expr) : int =
        match e with
        | Patterns.Value(v, _) when (v :? int) -> unbox<int> v
        | _ -> unbox<int> (Microsoft.FSharp.Linq.RuntimeHelpers.LeafExpressionConverter.EvaluateQuotation e)

    /// Flatten a selector body into an ordered list of (property, optional index) steps. An indexer
    /// (`xs.[i]`, however the quotation encodes it) attaches its index to the collection property
    /// that produced it.
    let rec private parseChain (e: Expr) : (PropertyInfo * int option) list =
        let attach target idx =
            match List.rev (parseChain target) with
            | (p, _) :: rest -> List.rev ((p, Some(evalIndex idx)) :: rest)
            | [] -> failwithf "Index in selector has no preceding collection property: %A" e
        match e with
        | Patterns.Var _ -> []
        | Patterns.PropertyGet(Some target, prop, []) -> parseChain target @ [ prop, None ]
        | Patterns.PropertyGet(Some target, _, [ idx ]) -> attach target idx
        | Patterns.Call(Some target, mi, [ idx ]) when mi.Name = "get_Item" -> attach target idx
        | Patterns.Call(None, mi, [ target; idx ]) when mi.Name = "GetArray" -> attach target idx
        | _ ->
            failwithf "Unsupported selector shape: %A. Use a property/index chain like <@ fun x -> x.A.[0].B @>." e

    /// Materialize a collection value's items (for index bounds checking and navigation).
    let private items (value: obj) : obj[] = (value :?> IEnumerable) |> Seq.cast<obj> |> Seq.toArray

    let private build (rootType: Type) (steps: (PropertyInfo * int option) list) (root: obj) : string =
        let sb = StringBuilder("/").Append(rootElementName rootType)
        let mutable current = root
        for (prop, idxOpt) in steps do
            match relativeStep prop with
            | None -> failwithf "Property '%s' is not serialized to XML." prop.Name
            | Some(piece, repeatable) ->
                let value = if isNull current then null else prop.GetValue current
                match idxOpt with
                | Some i ->
                    if not repeatable then
                        failwithf "Property '%s' is not a collection and cannot be indexed." prop.Name
                    if isNull value then failwithf "Collection '%s' is absent in this instance." prop.Name
                    let xs = items value
                    if i < 0 || i >= xs.Length then
                        failwithf "Index %d is out of range for '%s' (count %d)." i prop.Name xs.Length
                    sb.Append('/').Append(withIndex piece i) |> ignore
                    current <- xs.[i]
                | None ->
                    sb.Append('/').Append(piece) |> ignore
                    current <- value
        sb.ToString()

    /// Resolve the `#xpointer(...)` fragment selector for the property named by `selector` on the
    /// parsed `root` value, with real array positions taken from the indices in the quotation.
    let xpathOf (selector: Expr<'Root -> 'P>) (root: 'Root) : string =
        let steps =
            match (selector :> Expr) with
            | Patterns.Lambda(_, body) -> parseChain body
            | _ -> failwith "Expected a selector lambda, e.g. <@ fun x -> x.Prop @>."
        "#xpointer(" + build typeof<'Root> steps (box root) + ")"
