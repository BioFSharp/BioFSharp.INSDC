namespace BioFSharp.IO.INSDC

open OBO.NET

/// One decompiled leaf of a parsed INSDC record: the structural-ontology `Term` describing *what*
/// the value is, the concrete position-qualified `XPath` it was read from, and the `Value` as a
/// string. Produced by `<Entity>.decompile` — the ontology counterpart of `XPathEntry`, which only
/// says *where* a value lives. `Term` is the OBO.NET `OboTerm` from the generated structural ontology.
type DecompiledTerm =
    { Term: OboTerm
      XPath: string
      Value: string }


namespace BioFSharp.IO.INSDC

open System.IO
open System.Reflection
open System.Text.RegularExpressions

open OBO.NET

open BioFSharp.IO.INSDC.Internal

/// Loads the generated structural ontology (embedded `StructuralOntology.obo`, produced by the FAKE
/// target `generateStructuralOntology` from the `FragmentSelectors` maps) and joins a parsed record's
/// leaves to their ontology terms. Each leaf term carries its bare structural XPath as a
/// `property_value: insdc_xpath <xpath>`; a per-instance positional XPath is collapsed to that
/// structural form (`COLLABORATOR[2]` -> `COLLABORATOR`) before lookup. This is the runtime, OBO.NET
/// counterpart of the generator in `build/StructuralOntologyTasks.fs`.
module StructuralOntology =

    /// `property_value` marker prefix the generator writes the structural XPath behind.
    let [<Literal>] private xpathPrefix = "insdc_xpath "

    /// The embedded ontology, parsed once on first use via OBO.NET.
    let private oboOntology : Lazy<OboOntology> =
        lazy (
            let asm = Assembly.GetExecutingAssembly()
            let resource =
                asm.GetManifestResourceNames()
                |> Array.tryFind (fun n -> n.EndsWith "StructuralOntology.obo")
                |> Option.defaultWith (fun () ->
                    failwith "Embedded resource 'StructuralOntology.obo' was not found in BioFSharp.IO.INSDC.")
            use stream = asm.GetManifestResourceStream resource
            use reader = new StreamReader(stream)
            let lines = reader.ReadToEnd().Replace("\r\n", "\n").Split('\n') :> string seq
            OboOntology.fromLines false lines)

    /// The structural XPath a leaf term points at, recovered from its `insdc_xpath` property_value.
    let private xpathOfTerm (term: OboTerm) : string option =
        term.PropertyValues
        |> List.tryPick (fun pv ->
            if pv.StartsWith xpathPrefix then Some(pv.Substring xpathPrefix.Length) else None)

    /// structural XPath -> term, built once from the leaf terms of the ontology.
    let private xpathIndex : Lazy<Map<string, OboTerm>> =
        lazy (
            oboOntology.Value.Terms
            |> List.choose (fun t -> xpathOfTerm t |> Option.map (fun x -> x, t))
            |> Map.ofList)

    let private positionPredicate = Regex(@"\[\d+\]", RegexOptions.Compiled)

    /// The parsed structural ontology (forces the one-time load).
    let ontology () : OboOntology = oboOntology.Value

    /// Collapse a per-instance positional XPath to its structural form by dropping `[n]` predicates:
    /// `/PROJECT/COLLABORATORS/COLLABORATOR[2]/NAME` -> `/PROJECT/COLLABORATORS/COLLABORATOR/NAME`.
    let stripPositions (xpath: string) : string = positionPredicate.Replace(xpath, "")

    /// The structural-ontology term for a (positional or structural) XPath, or None if unmapped.
    let tryTermForXPath (xpath: string) : OboTerm option =
        Map.tryFind (stripPositions xpath) xpathIndex.Value

    /// Decompile a parsed `root` value into one `DecompiledTerm` per present leaf: its ontology term,
    /// the concrete positional XPath, and the string value. Leaves whose XPath has no term (should not
    /// happen — the ontology is generated from the same selector maps) are dropped.
    let decompile (root: 'Root) : DecompiledTerm list =
        XPathTracking.xpathEntries root
        |> Array.choose (fun e ->
            tryTermForXPath e.XPath
            |> Option.map (fun t -> { Term = t; XPath = e.XPath; Value = e.Value }))
        |> List.ofArray
