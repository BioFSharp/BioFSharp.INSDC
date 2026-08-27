namespace BioFSharp.INSDC.ArcIR

open System
open System.Collections.ObjectModel
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open BioFSharp.ArcIR
open BioFSharp.FileFormats.INSDC
open BioFSharp.IO.INSDC

/// An INSDC converter result paired with occurrence-level F1 field accounting.
type AccountedConversion =
    {
        /// Graph fragment produced by the existing typed converter.
        Conversion: ConversionResult
        /// One accounting outcome per present source XML leaf.
        Accounting: FieldAccountingReport
    }

/// XPointer-based accounting for typed INSDC-to-ArcIR conversion.
[<RequireQualifiedAccess>]
module F1Accounting =

    /// W3C specification governing the `xpointer(...)` selector scheme.
    let XPointerConformsTo = Iri.Create "https://www.w3.org/TR/xptr-xpointer/"

    [<Literal>]
    let private baseIri = "http://purl.org/arc/insdc/"

    [<Literal>]
    let private bioProjectSource = "INSDC BioProject"

    [<Literal>]
    let private studySource = "INSDC Study"

    let private modelledNamespaces =
        set [ "bioproject"; "study"; "biosample"; "sample"; "experiment"; "run"; "analysis"; "submission" ]

    let private sha256 (value: string) =
        use algorithm = SHA256.Create()

        algorithm.ComputeHash(Encoding.UTF8.GetBytes value)
        |> Array.map (fun (byte: byte) -> byte.ToString("x2"))
        |> String.concat ""

    let private escaped (value: string) = Uri.EscapeDataString(value.Trim())

    let private normalizedPath (path: string) =
        Regex.Replace(path, @"\[\d+\]", "[]")

    let private ruleId entity outcome path =
        Iri.Create(
            sprintf
                "urn:biofsharp:insdc:f1:rule:%s:%s:%s"
                entity
                outcome
                (sha256 (normalizedPath path))
        )

    let private reportId entity (artifact: ArtifactRevision) =
        Iri.Create(
            sprintf
                "urn:biofsharp:insdc:f1:diagnostic-report:%s:%s"
                entity
                (sha256 (artifact.Path + "\n" + artifact.Sha256))
        )

    let private sourceRef (artifact: ArtifactRevision) (entry: XPathEntry) : FragmentRef =
        { Artifact = artifact
          Selector =
            { ConformsTo = XPointerConformsTo
              Value = "#xpointer(" + entry.XPath + ")" } }

    let private unsupportedDiagnostic (entity: string) (path: string) (input: FragmentRef) : Diagnostic =
        let code = Iri.Create "urn:biofsharp:arcir:diagnostic-code:unsupported-source-field"

        { Id =
            Iri.Create(
                sprintf
                    "urn:biofsharp:arcir:diagnostic:f1:%s"
                    (sha256 (entity + "\n" + input.Artifact.Sha256 + "\n" + input.Selector.Value))
            )
          Code = code
          Severity = DiagnosticSeverity.Warning
          Message = sprintf "The %s F1 converter does not yet emit source field '%s'." entity path
          Targets = [ input ]
          Related = [] }

    let private annotationLocation (owner: Iri) (discriminator: Iri) (valueOnly: bool) =
        let assertionId = Identity.assertion owner "annotation" discriminator.Value

        if valueOnly then
            ArcJsonLocation.ObjectAnnotationValue(owner, assertionId)
        else
            ArcJsonLocation.ObjectAnnotation(owner, assertionId)

    let private annotationLocations (owner: Iri) (term: Iri) =
        [ annotationLocation owner term false
          annotationLocation owner term true ]

    let private structuralLocations (owner: Iri) (term: Iri) = annotationLocations owner term

    let private genericFieldTerm (source: string) (key: string) : Iri =
        (Annotations.field source key (ArcValue.String "")).Id

    let private genericLocations (owner: Iri) (source: string) (key: string) =
        genericFieldTerm source key |> annotationLocations owner

    let private attributeTerm index (attribute: BioFSharp.FileFormats.INSDC.Attribute) =
        if isNull attribute || String.IsNullOrWhiteSpace attribute.Tag then
            None
        else
            Some(Iri.Create(sprintf "%sattribute/%d/%s" baseIri (index + 1) (escaped attribute.Tag)))

    let private identifierTerm index kind =
        Iri.Create(sprintf "%sidentifier/%d/%s" baseIri (index + 1) (escaped kind))

    let private qualifiedIdentifierTerm index (identifier: QualifiedName) =
        let namespaceText =
            if String.IsNullOrWhiteSpace identifier.Namespace then "externalId" else identifier.Namespace

        identifierTerm index namespaceText

    let private relationLocation (subject: Iri) (predicate: Iri) (target: Iri) =
        ArcJsonLocation.Relation(Identity.relation subject predicate target)

    let private qualifiedIdentifierLocations
        (owner: Iri)
        (index: int)
        (identifier: QualifiedName)
        (memberName: string)
        =
        if isNull identifier || String.IsNullOrWhiteSpace identifier.Value then
            None
        else
            let term = qualifiedIdentifierTerm index identifier
            let assertion = annotationLocation owner term false
            let value = annotationLocation owner term true

            let relation =
                let namespaceText =
                    if String.IsNullOrWhiteSpace identifier.Namespace then "externalId" else identifier.Namespace

                if
                    modelledNamespaces.Contains(namespaceText.Trim().ToLowerInvariant())
                    && Identity.objectId identifier.Value <> owner
                then
                    Some(relationLocation owner Vocabulary.Rel.references (Identity.objectId identifier.Value))
                else
                    None

            match memberName with
            | "Value" -> Some(value :: Option.toList relation)
            | "Namespace" -> Some(assertion :: Option.toList relation)
            | _ -> None

    let private identifierLocations (owner: Iri) (identifiers: Identifier) (path: string) =
        if isNull identifiers then
            None
        else
            let indexed
                (pattern: string)
                (collection: Collection<Name>)
                (kind: string)
                =
                let matched = Regex.Match(path, pattern)

                if matched.Success then
                    let index = Int32.Parse matched.Groups.[1].Value

                    if index < collection.Count then
                        let identifier: Name = collection.[index]

                        if isNull identifier || String.IsNullOrWhiteSpace identifier.Value then
                            None
                        else
                            Some(annotationLocations owner (identifierTerm index kind))
                    else
                        None
                else
                    None

            match path with
            | "Identifiers.PrimaryId.Value" when not (isNull identifiers.PrimaryId) ->
                Some(annotationLocations owner (identifierTerm 0 "primaryId"))
            | "Identifiers.SubmitterId.Value" ->
                qualifiedIdentifierLocations owner 0 identifiers.SubmitterId "Value"
            | "Identifiers.SubmitterId.Namespace" ->
                qualifiedIdentifierLocations owner 0 identifiers.SubmitterId "Namespace"
            | _ ->
                indexed @"^Identifiers\.SecondaryId\[(\d+)\]\.Value$" identifiers.SecondaryId "secondaryId"
                |> Option.orElseWith (fun () ->
                    indexed @"^Identifiers\.Uuid\[(\d+)\]\.Value$" identifiers.Uuid "uuid")
                |> Option.orElseWith (fun () ->
                    let matched = Regex.Match(path, @"^Identifiers\.ExternalId\[(\d+)\]\.(Value|Namespace)$")

                    if matched.Success then
                        let index = Int32.Parse matched.Groups.[1].Value

                        if index < identifiers.ExternalId.Count then
                            qualifiedIdentifierLocations owner index identifiers.ExternalId.[index] matched.Groups.[2].Value
                        else
                            None
                    else
                        None)

    let private institutionLocations (rawOwner: string) (institution: string) =
        SubObjects.organization rawOwner institution
        |> Option.map (fun (node, relation) ->
            let nameProperty =
                node.Properties.Values
                |> Seq.find (fun property -> property.Predicate = Vocabulary.Property.ofName "Name")

            [ ArcJsonLocation.Object node.Id
              ArcJsonLocation.PropertyValue(node.Id, nameProperty.Id)
              ArcJsonLocation.Relation relation.Id ])

    let private attributeLocations
        (owner: Iri)
        (attributes: Collection<BioFSharp.FileFormats.INSDC.Attribute>)
        (path: string)
        (prefix: string)
        =
        let matched = Regex.Match(path, sprintf @"^%s\[(\d+)\]\.(Tag|Value|Units)$" prefix)

        if matched.Success then
            let index = Int32.Parse matched.Groups.[1].Value

            if index < attributes.Count then
                attributeTerm index attributes.[index]
                |> Option.map (fun term ->
                    match matched.Groups.[2].Value with
                    | "Tag" -> [ annotationLocation owner term false ]
                    | _ -> [ annotationLocation owner term true ])
            else
                None
        else
            None

    let private classifyBioProject (project: BioProject) (owner: Iri) (entry: XPathEntry) =
        let scalar =
            match entry.Path with
            | "Accession" -> Some(structuralLocations owner StructuralTerms.BioProject.archiveAccession)
            | "Alias" -> Some(genericLocations owner bioProjectSource "Alias")
            | "Name" -> Some(genericLocations owner bioProjectSource "Name")
            | "Title" -> Some(structuralLocations owner StructuralTerms.BioProject.title)
            | "Description" -> Some(structuralLocations owner StructuralTerms.BioProject.description)
            | "FirstPublicValue" -> Some(structuralLocations owner StructuralTerms.BioProject.firstPublicDate)
            | "CenterName" -> institutionLocations (Convert.entityId project) project.CenterName
            | "BrokerName" -> institutionLocations (Convert.entityId project) project.BrokerName
            | _ -> None

        scalar
        |> Option.orElseWith (fun () -> identifierLocations owner project.Identifiers entry.Path)
        |> Option.orElseWith (fun () ->
            attributeLocations owner project.ProjectAttributes entry.Path "ProjectAttributes")
        |> Option.orElseWith (fun () ->
            let matched =
                Regex.Match(
                    entry.Path,
                    @"^RelatedProjects\[(\d+)\]\.(ParentProject|ChildProject|PeerProject)\.Accession$"
                )

            if matched.Success then
                let index = Int32.Parse matched.Groups.[1].Value

                if index < project.RelatedProjects.Count then
                    let related = project.RelatedProjects.[index]

                    let predicate, accession =
                        match matched.Groups.[2].Value with
                        | "ParentProject" ->
                            Vocabulary.Rel.hasParentProject,
                            (if isNull related.ParentProject then null else related.ParentProject.Accession)
                        | "ChildProject" ->
                            Vocabulary.Rel.hasChildProject,
                            (if isNull related.ChildProject then null else related.ChildProject.Accession)
                        | _ ->
                            Vocabulary.Rel.hasPeerProject,
                            (if isNull related.PeerProject then null else related.PeerProject.Accession)

                    if String.IsNullOrWhiteSpace accession then
                        None
                    else
                        Some [ relationLocation owner predicate (Identity.objectId accession) ]
                else
                    None
            else
                None)

    let private classifyStudy (study: Study) (owner: Iri) (entry: XPathEntry) =
        let scalar =
            match entry.Path with
            | "Accession" -> Some(structuralLocations owner StructuralTerms.Study.archiveAccession)
            | "Descriptor.StudyTitle" -> Some(structuralLocations owner StructuralTerms.Study.title)
            | "Descriptor.StudyDescription" -> Some(structuralLocations owner StructuralTerms.Study.description)
            | "Descriptor.StudyAbstract" -> Some(genericLocations owner studySource "StudyAbstract")
            | "Descriptor.ProjectIdValue" -> Some(genericLocations owner studySource "ProjectId")
            | "Descriptor.Study.ExistingStudyType"
            | "Descriptor.Study.NewStudyType" -> Some(genericLocations owner studySource "StudyType")
            | "CenterName" -> institutionLocations (Convert.entityId study) study.CenterName
            | "BrokerName" -> institutionLocations (Convert.entityId study) study.BrokerName
            | _ -> None

        scalar
        |> Option.orElseWith (fun () -> identifierLocations owner study.Identifiers entry.Path)
        |> Option.orElseWith (fun () ->
            attributeLocations owner study.StudyAttributes entry.Path "StudyAttributes")

    let private account
        (entity: string)
        (artifact: ArtifactRevision)
        (classify: XPathEntry -> ArcJsonLocation list option)
        (entries: seq<XPathEntry>)
        =
        let diagnostics = ResizeArray<Diagnostic>()

        let accountingEntries =
            entries
            |> Seq.map (fun entry ->
                let input = sourceRef artifact entry

                match classify entry with
                | Some outputs ->
                    { RuleId = ruleId entity "emit" entry.Path
                      Input = input
                      Outcome = FieldAccountingOutcome.Emitted outputs }
                | None ->
                    let diagnostic = unsupportedDiagnostic entity entry.Path input
                    diagnostics.Add diagnostic

                    { RuleId = ruleId entity "unsupported" entry.Path
                      Input = input
                      Outcome = FieldAccountingOutcome.Unsupported diagnostic.Id })
            |> List.ofSeq

        let diagnosticReport =
            DiagnosticReport.create (reportId entity artifact) diagnostics

        FieldAccounting.create diagnosticReport accountingEntries

    /// Converts and accounts for every present leaf in one BioProject source artifact.
    let bioProject (artifact: ArtifactRevision) (project: BioProject) =
        let conversion = BioProjectConversion.convert project
        let owner = Identity.objectId (Convert.entityId project)

        { Conversion = conversion
          Accounting =
            BioFSharp.IO.INSDC.BioProject.xpathEntries project
            |> account "bioproject" artifact (classifyBioProject project owner) }

    /// Converts and accounts for every present leaf in one Study source artifact.
    let study (artifact: ArtifactRevision) (study: Study) =
        let conversion = StudyConversion.convert study
        let owner = Identity.objectId (Convert.entityId study)

        { Conversion = conversion
          Accounting =
            BioFSharp.IO.INSDC.Study.xpathEntries study
            |> account "study" artifact (classifyStudy study owner) }
