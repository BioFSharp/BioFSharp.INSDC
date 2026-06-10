module FragmentSelectorTasks

open System
open System.IO
open System.Text
open System.Reflection
open System.Collections.Generic

open BlackFox.Fake
open Fake.DotNet
open Fake.IO.Globbing.Operators

// Reflection engine + `generateFragmentSelectors` FAKE target.
//
// For every entity root type in BioFSharp.FileFormats.INSDC we emit a `partial class`
// companion exposing `FragmentSelectors : IReadOnlyDictionary<string,string>` that maps each
// property (by dotted path from the document root) to its W3C XPointer fragment selector, e.g.
//   BioProject.FragmentSelectors["Accession"] = "#xpointer(/PROJECT/@accession)"
//
// The selectors are derived from the same `System.Xml.Serialization` attributes the serializer
// uses, so they cannot drift from the model. On-demand only — like `regenerateInsdcTypes` the
// default build does NOT depend on this; run it after touching schemas/ or the type model.
module private Engine =

    /// Escape a string for embedding in a C# verbatim-free double-quoted literal.
    let private escape (s: string) =
        s.Replace("\\", "\\\\").Replace("\"", "\\\"")

    /// `Assembly.GetTypes` but tolerant of types that fail to load.
    let private getTypes (a: Assembly) =
        try a.GetTypes()
        with :? ReflectionTypeLoadException as ex -> ex.Types |> Array.filter (isNull >> not)

    /// Read an XML name off an attribute: the first non-empty string ctor arg, else a named arg.
    let private nameFrom (namedKey: string) (cad: CustomAttributeData) : string option =
        let fromCtor =
            cad.ConstructorArguments
            |> Seq.tryPick (fun a ->
                match a.Value with
                | :? string as s when not (String.IsNullOrEmpty s) -> Some s
                | _ -> None)
        match fromCtor with
        | Some _ -> fromCtor
        | None ->
            cad.NamedArguments
            |> Seq.tryPick (fun na ->
                if na.MemberName = namedKey then
                    match na.TypedValue.Value with
                    | :? string as s when not (String.IsNullOrEmpty s) -> Some s
                    | _ -> None
                else None)

    /// Unwrap a collection/array property type to its element type (leaving non-collections as-is).
    let private unwrap (t: Type) : Type =
        if t = typeof<string> then t
        elif t.IsArray then t.GetElementType()
        else
            Seq.append [ t ] (t.GetInterfaces() :> seq<_>)
            |> Seq.tryPick (fun i ->
                if i.IsGenericType && i.GetGenericTypeDefinition() = typedefof<IEnumerable<_>>
                then Some(i.GetGenericArguments().[0])
                else None)
            |> Option.defaultValue t

    /// The complex (descend-into) model type a property points at, if any.
    let private childOf (modelAsm: Assembly) (t: Type) : Type option =
        let u = unwrap t
        if u.Assembly = modelAsm && u.IsClass then Some u else None

    /// Relative XPath step(s) for a property, each with the complex child type to descend into.
    /// Returns [] for `[XmlIgnore]` and unannotated members.
    let private propSteps (modelAsm: Assembly) (p: PropertyInfo) : (string * Type option) list =
        let cads = p.GetCustomAttributesData()
        let has n = cads |> Seq.exists (fun c -> c.AttributeType.Name = n)
        let tryGet n = cads |> Seq.tryFind (fun c -> c.AttributeType.Name = n)
        if has "XmlIgnoreAttribute" then []
        else
            match tryGet "XmlAttributeAttribute" with
            | Some cad ->
                let n = nameFrom "AttributeName" cad |> Option.defaultValue p.Name
                [ "@" + n, None ]
            | None ->
                match tryGet "XmlArrayAttribute" with
                | Some arr ->
                    let arrName = nameFrom "ElementName" arr |> Option.defaultValue p.Name
                    let itemName =
                        tryGet "XmlArrayItemAttribute"
                        |> Option.bind (nameFrom "ElementName")
                        |> Option.defaultValue arrName
                    [ arrName + "/" + itemName, childOf modelAsm p.PropertyType ]
                | None ->
                    let elems =
                        cads
                        |> Seq.filter (fun c -> c.AttributeType.Name = "XmlElementAttribute")
                        |> Seq.toList
                    if not (List.isEmpty elems) then
                        elems
                        |> List.map (fun cad ->
                            let n = nameFrom "ElementName" cad |> Option.defaultValue p.Name
                            n, childOf modelAsm p.PropertyType)
                    elif has "XmlTextAttribute" then [ "text()", None ]
                    else []

    /// The root XML element name for a type carrying `[XmlRoot]`, if any.
    let private rootElementName (t: Type) =
        t.GetCustomAttributesData()
        |> Seq.tryPick (fun c ->
            if c.AttributeType.Name = "XmlRootAttribute" then nameFrom "ElementName" c else None)

    /// Walk a root type, accumulating (dottedPropertyPath, xpath) for every leaf.
    let private buildLeaves (modelAsm: Assembly) (rootName: string) (rootType: Type) : (string * string) list =
        let acc = ResizeArray<string * string>()
        let visited = HashSet<Type>()
        let rec go depth (keyPrefix: string) (xpathPrefix: string) (t: Type) =
            if depth <= 64 && visited.Add t then
                let props =
                    t.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
                    |> Array.filter (fun p -> p.CanRead && p.GetIndexParameters().Length = 0)
                for p in props do
                    for (step, child) in propSteps modelAsm p do
                        let key = if keyPrefix = "" then p.Name else keyPrefix + "." + p.Name
                        let xpath = xpathPrefix + "/" + step
                        match child with
                        | Some ct when not (visited.Contains ct) -> go (depth + 1) key xpath ct
                        | _ -> acc.Add(key, xpath) // leaf, or a cycle we stop at
                visited.Remove t |> ignore
        go 0 "" ("/" + rootName) rootType
        acc |> List.ofSeq

    /// Deterministic ordering + duplicate-key disambiguation (xscgen choice groups).
    let private finalize (pairs: (string * string) list) : (string * string) list =
        let sorted =
            pairs
            |> List.sortWith (fun (k1, v1) (k2, v2) ->
                let c = String.CompareOrdinal(k1, k2)
                if c <> 0 then c else String.CompareOrdinal(v1, v2))
        let disambiguated =
            sorted
            |> List.groupBy fst
            |> List.collect (fun (k, group) ->
                match group with
                | [ single ] -> [ single ]
                | many ->
                    many
                    |> List.map (fun (_, v) ->
                        let last = v.Substring(v.LastIndexOf('/') + 1).TrimStart('@')
                        (sprintf "%s(%s)" k last), v))
        let seen = Dictionary<string, int>()
        disambiguated
        |> List.map (fun (k, v) ->
            match seen.TryGetValue k with
            | true, n -> seen.[k] <- n + 1; (sprintf "%s#%d" k (n + 1)), v
            | _ -> seen.[k] <- 0; (k, v))
        |> List.sortWith (fun (k1, _) (k2, _) -> String.CompareOrdinal(k1, k2))

    /// Generate the full FragmentSelectors.cs content for the model assembly.
    let generate (asm: Assembly) : string =
        // Entity roots only: skip the `*_SET` wrapper roots (containers, not entities).
        let roots =
            getTypes asm
            |> Array.choose (fun t ->
                match rootElementName t with
                | Some rn when t.IsClass && not t.IsAbstract && not (rn.EndsWith "_SET") -> Some(t, rn)
                | _ -> None)
            |> Array.sortBy (fun (t, _) -> t.Name)

        let sb = StringBuilder()
        sb.AppendLine("//------------------------------------------------------------------------------") |> ignore
        sb.AppendLine("// <auto-generated>") |> ignore
        sb.AppendLine("//     Generated by the FAKE build target `generateFragmentSelectors`.") |> ignore
        sb.AppendLine("//     Maps each property (dotted path from the document root) to its W3C XPointer") |> ignore
        sb.AppendLine("//     fragment selector. Do not edit by hand.") |> ignore
        sb.AppendLine("//     Regenerate with: ./build.sh generateFragmentSelectors") |> ignore
        sb.AppendLine("// </auto-generated>") |> ignore
        sb.AppendLine("//------------------------------------------------------------------------------") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("namespace BioFSharp.FileFormats.INSDC") |> ignore
        sb.AppendLine("{") |> ignore

        let mutable first = true
        for (t, rootName) in roots do
            let pairs = buildLeaves asm rootName t |> finalize
            if not (List.isEmpty pairs) then
                if not first then sb.AppendLine() |> ignore
                first <- false
                sb.AppendLine(sprintf "    public partial class %s" t.Name) |> ignore
                sb.AppendLine("    {") |> ignore
                sb.AppendLine("        /// <summary>") |> ignore
                sb.AppendLine(sprintf "        /// Maps each %s property (dotted path from the document root) to its W3C" t.Name) |> ignore
                sb.AppendLine("        /// XPointer fragment selector. Auto-generated by build target generateFragmentSelectors.") |> ignore
                sb.AppendLine("        /// </summary>") |> ignore
                sb.AppendLine("        public static readonly System.Collections.Generic.IReadOnlyDictionary<string, string> FragmentSelectors =") |> ignore
                sb.AppendLine("            new System.Collections.Generic.Dictionary<string, string>") |> ignore
                sb.AppendLine("            {") |> ignore
                for (k, xpath) in pairs do
                    let v = "#xpointer(" + xpath + ")"
                    sb.AppendLine(sprintf "                [\"%s\"] = \"%s\"," (escape k) (escape v)) |> ignore
                sb.AppendLine("            };") |> ignore
                sb.AppendLine("    }") |> ignore

        sb.AppendLine("}") |> ignore
        sb.ToString()

/// Regenerate `src/BioFSharp.FileFormats.INSDC/FragmentSelectors.cs` from the built type model.
/// On-demand only; the default build does NOT depend on this target.
let generateFragmentSelectors =
    BuildTask.create "generateFragmentSelectors" [] {
        let projectDir = "src/BioFSharp.FileFormats.INSDC"
        let proj = projectDir + "/BioFSharp.FileFormats.INSDC.csproj"
        let outFile = projectDir + "/FragmentSelectors.cs"

        // Remove any prior output first so a stale/broken file can't block the build we reflect over.
        if File.Exists outFile then File.Delete outFile

        proj
        |> DotNet.build (fun p ->
            { p with MSBuildParams = { p.MSBuildParams with DisableInternalBinLog = true } })

        let dll =
            !!(projectDir + "/bin/**/BioFSharp.FileFormats.INSDC.dll")
            |> Seq.sortByDescending File.GetLastWriteTimeUtc
            |> Seq.tryHead
            |> Option.defaultWith (fun () ->
                failwith "Could not locate built BioFSharp.FileFormats.INSDC.dll to reflect over.")

        let asm = Assembly.LoadFrom(Path.GetFullPath dll)
        File.WriteAllText(outFile, Engine.generate asm)
        printfn "Wrote fragment selectors to %s" outFile
    }
