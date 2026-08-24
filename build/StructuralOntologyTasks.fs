module StructuralOntologyTasks

open System
open System.IO
open System.Text
open System.Reflection
open System.Collections.Generic

open BlackFox.Fake
open Fake.DotNet
open Fake.IO.Globbing.Operators

open OBO.NET

// Generator + `generateStructuralOntology` FAKE target.
//
// Emits a committed OBO ontology (`src/BioFSharp.IO.INSDC/StructuralOntology.obo`) whose terms
// mirror the INSDC XML structure. It is built *from the existing `FragmentSelectors` maps* in
// BioFSharp.FileFormats.INSDC — for every leaf `(dottedPath -> #xpointer(xpath))` entry we synthesize
// the chain of container terms (one per dotted prefix, plus an entity root) wired with `part_of`, and
// a leaf term carrying the bare structural xpath as a `property_value` (`insdc_xpath /PROJECT/NAME`).
// That property_value is the join key the IO layer's `decompile` uses; the OBO IO itself goes through
// OBO.NET.
//
// One reflection step on top of the selector strings: the FragmentSelectors key is the F# *property*
// path, and xscgen collapses a `[XmlArray]` wrapper+item pair into a single property (so the item level
// is in the xpath but not the key). We re-derive those wrapped-collection item levels from the model
// and splice them back into the dotted path (`ProjectAttributes.Tag` -> `ProjectAttributes.Attribute.Tag`)
// so the ontology mirrors the XML, not the flatter property graph. Names are labels only; the xpath
// join key is untouched, so `decompile` is unaffected.
//
// On-demand only — like `generateFragmentSelectors` the default build does NOT depend on this; run it
// after touching schemas/, the type model, or FragmentSelectors.cs.
module private Engine =

    /// Strip the `#xpointer( ... )` wrapper off a fragment selector, leaving the bare XPath.
    let private bareXPath (xpointer: string) : string =
        let prefix = "#xpointer("
        if xpointer.StartsWith prefix && xpointer.EndsWith ")" then
            xpointer.Substring(prefix.Length, xpointer.Length - prefix.Length - 1)
        else xpointer

    /// Read the generated `public static FragmentSelectors` dictionary off an entity type, if present.
    let private fragmentSelectors (t: Type) : (string * string) list option =
        match t.GetField("FragmentSelectors", BindingFlags.Public ||| BindingFlags.Static) with
        | null -> None
        | f ->
            match f.GetValue null with
            | :? IReadOnlyDictionary<string, string> as d ->
                d |> Seq.map (fun kv -> kv.Key, bareXPath kv.Value) |> List.ofSeq |> Some
            | _ -> None

    /// Unwrap a collection/array property type to its element type (mirrors the FragmentSelectors
    /// generator). Leaves non-collections as-is.
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

    /// Per entity root type, the dotted *property* path of every wrapped collection (`[XmlArray]`,
    /// e.g. `<PROJECT_ATTRIBUTES><PROJECT_ATTRIBUTE>…`) mapped to its element type name. xscgen models
    /// such a wrapper+item pair as a single property named after the wrapper, dropping the item level
    /// from the property path — but the XML (and this ontology, which mirrors it) keeps it. We splice
    /// the element type name back in as that level: `ProjectAttributes` -> `ProjectAttributes.Attribute`.
    /// Unwrapped repeated elements (xscgen emits `[XmlElement]` on the collection, named after the item
    /// itself, e.g. `SecondaryId`) already carry their level and are not recorded here.
    let private wrappedItemLevels (modelAsm: Assembly) (rootType: Type) : Map<string, string> =
        let acc = Dictionary<string, string>()
        let visited = HashSet<Type>()
        let has n (p: PropertyInfo) =
            p.GetCustomAttributesData() |> Seq.exists (fun c -> c.AttributeType.Name = n)
        let complexChild (t: Type) =
            let u = unwrap t
            if u.Assembly = modelAsm && u.IsClass then Some u else None
        let rec go depth (keyPrefix: string) (t: Type) =
            if depth <= 64 && visited.Add t then
                t.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
                |> Array.filter (fun p -> p.CanRead && p.GetIndexParameters().Length = 0 && not (has "XmlIgnoreAttribute" p))
                |> Array.iter (fun p ->
                    let key = if keyPrefix = "" then p.Name else keyPrefix + "." + p.Name
                    match complexChild p.PropertyType with
                    | Some ct ->
                        if has "XmlArrayAttribute" p then acc.[key] <- (unwrap p.PropertyType).Name
                        if not (visited.Contains ct) then go (depth + 1) key ct
                    | None -> ())
                visited.Remove t |> ignore
        go 0 "" rootType
        acc |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

    /// Rewrite a dotted property-path key, splicing each wrapped-collection item level (the element type
    /// name) in right after its wrapper prefix, so the dotted path mirrors the full XML element chain.
    /// `running` tracks the original *property* path (suffix-free for non-terminal segments, and the
    /// disambiguation suffix on the terminal segment never matches a container), so insertions don't
    /// feed back into later lookups.
    let private spliceItemLevels (levels: Map<string, string>) (key: string) : string =
        let out = ResizeArray<string>()
        let mutable running = ""
        for seg in key.Split('.') do
            running <- if running = "" then seg else running + "." + seg
            out.Add seg
            match Map.tryFind running levels with
            | Some itemName -> out.Add itemName
            | None -> ()
        String.Join(".", out)

    /// A node of one entity's structural tree. `DottedPath = ""` is the entity root; `Parent` is the
    /// parent's dotted path within the same entity (`Some ""` = the root), `None` only for the root.
    type private Node =
        { Entity: string
          DottedPath: string
          IsLeaf: bool
          XPath: string
          Parent: string option }

    /// Proper dotted prefixes of a key: `A.B.C` -> [`A`; `A.B`]. The disambiguation suffixes the
    /// FragmentSelectors engine appends (`Foo(bar)`, `Foo#2`) only ever land on the terminal segment
    /// and contain no dots, so splitting on `.` keeps the container chain clean.
    let private prefixes (key: string) : string list =
        let parts = key.Split('.')
        [ for i in 1 .. parts.Length - 1 -> String.Join(".", parts.[0 .. i - 1]) ]

    /// The parent dotted path of a non-root node (`Some ""` when it is a top-level field).
    let private parentOf (dotted: string) : string option =
        let i = dotted.LastIndexOf '.'
        if i < 0 then Some "" else Some(dotted.Substring(0, i))

    /// All structural nodes for one entity: the root, one container per distinct dotted prefix, and a
    /// leaf per selector entry.
    let private entityNodes (entity: string) (pairs: (string * string) list) : Node list =
        let root =
            { Entity = entity; DottedPath = ""; IsLeaf = false; XPath = ""; Parent = None }
        let containers =
            pairs
            |> List.collect (fst >> prefixes)
            |> List.distinct
            |> List.map (fun p ->
                { Entity = entity; DottedPath = p; IsLeaf = false; XPath = ""; Parent = parentOf p })
        let leaves =
            pairs
            |> List.map (fun (k, xpath) ->
                { Entity = entity; DottedPath = k; IsLeaf = true; XPath = xpath; Parent = parentOf k })
        root :: containers @ leaves

    let private idOf (n: int) : string = sprintf "INSDC:%07d" n

    /// Generate the full StructuralOntology.obo content for the model assembly.
    let generate (asm: Assembly) : string =
        let entities =
            asm.GetTypes()
            |> Array.choose (fun t -> fragmentSelectors t |> Option.map (fun ps -> t, ps))
            |> Array.map (fun (t, ps) ->
                // Splice the dropped wrapped-collection item level back into every key so names and the
                // container chain mirror the XML element structure, not the (flatter) property path.
                let levels = wrappedItemLevels asm t
                t.Name, ps |> List.map (fun (k, xpath) -> spliceItemLevels levels k, xpath))
            |> Array.sortBy fst

        // Deterministic global numbering: entity, then dotted path. A dotted prefix sorts before any
        // path it is a prefix of, so containers always precede their own leaves -> stable ids.
        let allNodes =
            entities
            |> Array.collect (fun (entity, ps) -> entityNodes entity ps |> List.toArray)
            |> Array.sortWith (fun a b ->
                let c = String.CompareOrdinal(a.Entity, b.Entity)
                if c <> 0 then c else String.CompareOrdinal(a.DottedPath, b.DottedPath))

        let ids =
            allNodes
            |> Array.mapi (fun i n -> (n.Entity, n.DottedPath), idOf (i + 1))
            |> Map.ofArray

        let terms =
            allNodes
            |> Array.map (fun n ->
                let id = ids.[(n.Entity, n.DottedPath)]
                // Entity-qualify every non-root name so it mirrors the full XPath (the entity is the
                // first XPath segment) and stays globally unique: a leaf nested under the `Webin`
                // wrapper (`/WEBIN/EXPERIMENT/...`) and the same field on the standalone `Experiment`
                // record (`/EXPERIMENT/...`) must not collapse to the same `Experiment.Platform...` name.
                let name = if n.DottedPath = "" then n.Entity else n.Entity + "." + n.DottedPath
                let def =
                    if n.IsLeaf then sprintf "INSDC %s field %s at %s" n.Entity n.DottedPath n.XPath
                    elif n.DottedPath = "" then sprintf "INSDC %s record root" n.Entity
                    else sprintf "INSDC %s structural group %s" n.Entity n.DottedPath
                let relationships =
                    match n.Parent with
                    | Some parent -> [ "part_of " + ids.[(n.Entity, parent)] ]
                    | None -> []
                let propertyValues = if n.IsLeaf then [ "insdc_xpath " + n.XPath ] else []
                OboTerm.Create(
                    id,
                    Name = name,
                    Definition = def,
                    Relationships = relationships,
                    PropertyValues = propertyValues))
            |> List.ofArray

        let partOf = OboTypeDef.Create("part_of", "", "", Name = "part of", Is_transitive = true)
        let onto = OboOntology.Create(terms, [ partOf ], "1.2", Ontology = "insdc-structural")

        // OboOntology.toLines emits only the [Term]/[Typedef] stanzas; prepend a (date-free, so it
        // stays byte-idempotent) OBO header by hand.
        let header =
            [ "format-version: 1.2"
              "ontology: insdc-structural"
              "auto-generated-by: generateStructuralOntology"
              "remark: Auto-generated from BioFSharp.FileFormats.INSDC FragmentSelectors. Do not edit by hand."
              "remark: Regenerate with ./build.sh generateStructuralOntology"
              "" ]
        String.Join("\n", Seq.append header (OboOntology.toLines onto)) + "\n"

let private modelDirectory = "src/BioFSharp.FileFormats.INSDC"
let private modelProject = modelDirectory + "/BioFSharp.FileFormats.INSDC.csproj"
let private outputFile = "src/BioFSharp.IO.INSDC/StructuralOntology.obo"

let private findModelAssembly () =
    !!(modelDirectory + "/bin/**/BioFSharp.FileFormats.INSDC.dll")
    |> Seq.sortByDescending File.GetLastWriteTimeUtc
    |> Seq.tryHead
    |> Option.defaultWith (fun () ->
        failwith "Could not locate built BioFSharp.FileFormats.INSDC.dll to reflect over.")

/// Computes the canonical ontology source from the already-built type model.
let generatedStructuralOntologyContent () =
    let asm = findModelAssembly () |> Path.GetFullPath |> Assembly.LoadFrom
    (Engine.generate asm).Replace("\r\n", "\n").Replace("\r", "\n")

let private writeCanonical (path: string) (content: string) =
    File.WriteAllText(path, content, UTF8Encoding(false))

/// Regenerate `src/BioFSharp.IO.INSDC/StructuralOntology.obo` from the built type model's
/// FragmentSelectors maps. On-demand only; the default build does NOT depend on this target.
let generateStructuralOntology =
    BuildTask.create "generateStructuralOntology" [] {

        modelProject
        |> DotNet.build (fun p ->
            { p with MSBuildParams = { p.MSBuildParams with DisableInternalBinLog = true } })

        writeCanonical outputFile (generatedStructuralOntologyContent ())
        printfn "Wrote structural ontology to %s" outputFile
    }
