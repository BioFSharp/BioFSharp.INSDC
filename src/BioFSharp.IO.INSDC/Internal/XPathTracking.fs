namespace BioFSharp.IO.INSDC

/// A single leaf of a parsed INSDC record: its F# property path (with array positions), the absolute
/// positional XPath to the value in the source XML, and the value as a string. A plain serializable
/// record for web/API DTOs — one per present leaf of a parsed entity, position-qualified.
type XPathEntry =
    { Path: string
      XPath: string
      Value: string }


namespace BioFSharp.IO.INSDC.Internal

open System
open System.Collections
open System.Collections.Generic
open System.Reflection
open System.Text
open System.Xml.Serialization

open Microsoft.FSharp.Quotations

open BioFSharp.IO.INSDC

/// Resolves the concrete, position-qualified XPath of a property on a parsed INSDC value. Public
/// entity modules expose this as `<Entity>.xpathOf` (the bare XPath) and `<Entity>.xpointerOf` (the
/// same path wrapped as a W3C `#xpointer(...)` fragment). The property is named with a quotation
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

    /// The bare absolute XPath for the property named by `selector` on the parsed `root` value
    /// (`/PROJECT/NAME`), with real array positions taken from the indices in the quotation.
    let xpathOf (selector: Expr<'Root -> 'P>) (root: 'Root) : string =
        let steps =
            match (selector :> Expr) with
            | Patterns.Lambda(_, body) -> parseChain body
            | _ -> failwith "Expected a selector lambda, e.g. <@ fun x -> x.Prop @>."
        build typeof<'Root> steps (box root)

    /// The same selector as `xpathOf`, wrapped as a W3C XPointer fragment (`#xpointer(/PROJECT/NAME)`).
    let xpointerOf (selector: Expr<'Root -> 'P>) (root: 'Root) : string =
        "#xpointer(" + xpathOf selector root + ")"

    /// True for a model class to descend into (vs. a leaf: string, primitive, enum, DateTime, ...).
    let private isModelClass (modelAsm: Assembly) (t: Type) : bool =
        t.IsClass && t <> typeof<string> && t.Assembly = modelAsm

    /// The string form of a leaf value. Equals the source XML text for strings; enum/date leaves use
    /// the invariant CLR form, which can differ from the raw serialized text.
    let private stringify (v: obj) : string =
        match v with
        | null -> null
        | :? string as s -> s
        | :? bool as b -> if b then "true" else "false"
        | :? IFormattable as f -> f.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
        | other -> other.ToString()

    /// Walk a parsed `root` value and return one `XPathEntry` per present leaf, with array positions
    /// (`COLLABORATOR[2]`) taken from the data — a serializable, position-qualified view of the whole
    /// record for use in DTOs. Containers are not emitted; only addressable leaf values.
    let xpathEntries (root: 'Root) : XPathEntry[] =
        let modelAsm = typeof<'Root>.Assembly
        let acc = ResizeArray<XPathEntry>()
        let appendKey prefix seg = if prefix = "" then seg else prefix + "." + seg
        let rec emit (key: string) (xpath: string) (value: obj) (depth: int) =
            if not (isNull value) then
                if isModelClass modelAsm (value.GetType()) then walk key xpath value (depth + 1)
                else acc.Add { Path = key; XPath = xpath; Value = stringify value }
        and walk (keyPrefix: string) (xpathPrefix: string) (node: obj) (depth: int) =
            if not (isNull node) && depth <= 64 then
                node.GetType().GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
                |> Array.filter (fun p -> p.CanRead && p.GetIndexParameters().Length = 0)
                |> Array.iter (fun p ->
                    match relativeStep p with
                    | None -> ()
                    | Some(piece, repeatable) ->
                        let value = p.GetValue node
                        if not (isNull value) then
                            if repeatable then
                                items value
                                |> Array.iteri (fun i item ->
                                    emit (appendKey keyPrefix (sprintf "%s[%d]" p.Name i))
                                         (xpathPrefix + "/" + withIndex piece i) item depth)
                            else
                                emit (appendKey keyPrefix p.Name) (xpathPrefix + "/" + piece) value depth)
        walk "" ("/" + rootElementName typeof<'Root>) (box root) 0
        acc.ToArray()
