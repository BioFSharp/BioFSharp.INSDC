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

    [<Literal>]
    let private bioSampleSource = "INSDC BioSample"

    [<Literal>]
    let private experimentSource = "INSDC Experiment"

    [<Literal>]
    let private runSource = "INSDC Run"

    [<Literal>]
    let private analysisSource = "INSDC Analysis"

    [<Literal>]
    let private submissionSource = "INSDC Submission"

    [<Literal>]
    let private receiptSource = "INSDC Receipt"

    type private FieldClassification =
        | Emission of ArcJsonLocation list
        | IntentionalOmission of string

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

    let private emitted outputs = Some(Emission outputs)

    let private ignored reason = Some(IntentionalOmission reason)

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

    let private subObjectPropertyLocations
        (node: ArcObject)
        (relation: ArcRelation)
        (propertyName: string)
        =
        let predicate = Vocabulary.Property.ofName propertyName

        node.Properties.Values
        |> Seq.tryFind (fun property -> property.Predicate = predicate)
        |> Option.map (fun property ->
            [ ArcJsonLocation.Object node.Id
              ArcJsonLocation.PropertyValue(node.Id, property.Id)
              ArcJsonLocation.Relation relation.Id ])

    let private allSubObjectPropertyLocations (node: ArcObject) (relation: ArcRelation) =
        [ ArcJsonLocation.Object node.Id
          yield!
              node.Properties.Values
              |> Seq.map (fun property -> ArcJsonLocation.PropertyValue(node.Id, property.Id))
          ArcJsonLocation.Relation relation.Id ]

    let private identityLocations (owner: Iri) (accession: string) (path: string) =
        match path with
        | "Accession" -> emitted [ ArcJsonLocation.Object owner ]
        | "Alias" when String.IsNullOrWhiteSpace accession -> emitted [ ArcJsonLocation.Object owner ]
        | "Alias" -> ignored "The archive accession, rather than the duplicate alias, supplied the ArcIR object identity."
        | _ -> None

    let private referenceLocations
        (owner: Iri)
        (predicate: Iri)
        (reference: RefObject)
        (prefix: string)
        (path: string)
        =
        if isNull reference || not (path.StartsWith(prefix + ".", StringComparison.Ordinal)) then
            None
        else
            let memberPath = path.Substring(prefix.Length + 1)

            let target =
                if not (String.IsNullOrWhiteSpace reference.Accession) then
                    Some(Identity.objectId reference.Accession)
                elif not (String.IsNullOrWhiteSpace reference.Refname) then
                    Some(Identity.objectId reference.Refname)
                else
                    None

            match memberPath, target with
            | "Accession", Some target -> emitted [ relationLocation owner predicate target ]
            | "Refname", Some _ when not (String.IsNullOrWhiteSpace reference.Accession) ->
                ignored "The archive accession was authoritative; refname was retained only as duplicate reference metadata."
            | "Refcenter", Some _ when not (String.IsNullOrWhiteSpace reference.Accession) ->
                ignored "The archive accession was authoritative; refcenter was retained only as duplicate reference metadata."
            | "Refname", Some target
            | "Refcenter", Some target -> emitted [ relationLocation owner predicate target ]
            | memberName, _ when memberName.StartsWith("Identifiers.", StringComparison.Ordinal) ->
                ignored "The nested identifier block duplicates the reference resolved from accession/refname."
            | _ -> None

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

    let private organismLocations
        (rawOwner: string)
        (name: BioSampleName)
        (path: string)
        =
        if isNull name || not (path.StartsWith("SampleName.", StringComparison.Ordinal)) then
            None
        else
            let propertyName = path.Substring("SampleName.".Length)
            let node, relation = SubObjects.organism rawOwner name
            subObjectPropertyLocations node relation propertyName
            |> Option.map Emission

    let private instrumentLocations (rawOwner: string) (platform: Platform) (path: string) =
        if
            isNull platform
            || not (path.StartsWith("Platform.", StringComparison.Ordinal))
            || not (path.EndsWith(".InstrumentModel", StringComparison.Ordinal))
        then
            None
        else
            SubObjects.instrument rawOwner platform
            |> Option.map (fun (node, relation) -> Emission(allSubObjectPropertyLocations node relation))

    let private protocolLocations
        (rawOwner: string)
        (library: LibraryDescriptor)
        (entry: XPathEntry)
        =
        let prefix = "Design.LibraryDescriptor."

        if isNull library || not (entry.Path.StartsWith(prefix, StringComparison.Ordinal)) then
            None
        else
            let propertyName = entry.Path.Substring(prefix.Length)

            match propertyName with
            | "LibraryName"
            | "PoolingStrategy"
            | "LibraryConstructionProtocol" when String.IsNullOrWhiteSpace entry.Value ->
                ignored "The present XML field was blank, and the converter intentionally omits blank string assertions."
            | "LibraryName"
            | "LibraryStrategy"
            | "LibrarySource"
            | "LibrarySelection"
            | "PoolingStrategy"
            | "LibraryConstructionProtocol" ->
                let node, relation = SubObjects.protocol rawOwner library

                subObjectPropertyLocations node relation propertyName
                |> Option.map Emission
            | _ -> None

    let private bioSampleReferenceLocations
        (owner: Iri)
        (reference: RefObject)
        (prefix: string)
        (path: string)
        =
        if isNull reference || not (path.StartsWith(prefix + ".", StringComparison.Ordinal)) then
            None
        else
            let bioSampleExternal =
                if isNull reference.Identifiers then
                    None
                else
                    reference.Identifiers.ExternalId
                    |> Seq.mapi (fun index identifier -> index, identifier)
                    |> Seq.tryFind (fun (_, identifier) ->
                        not (isNull identifier)
                        && String.Equals(identifier.Namespace, "BioSample", StringComparison.OrdinalIgnoreCase)
                        && not (String.IsNullOrWhiteSpace identifier.Value))

            match bioSampleExternal with
            | None -> referenceLocations owner Vocabulary.Rel.hasSample reference prefix path
            | Some(index, externalIdentifier) ->
                let relative = path.Substring(prefix.Length + 1)
                let selectedPrefix = sprintf "Identifiers.ExternalId[%d]." index

                if relative.StartsWith(selectedPrefix, StringComparison.Ordinal) then
                    match relative.Substring(selectedPrefix.Length) with
                    | "Namespace"
                    | "Value" ->
                        emitted
                            [ relationLocation
                                  owner
                                  Vocabulary.Rel.hasSample
                                  (Identity.objectId externalIdentifier.Value) ]
                    | "Label" -> ignored "The external-identifier label is descriptive and does not affect sample resolution."
                    | _ -> None
                elif relative.StartsWith("Identifiers.", StringComparison.Ordinal) then
                    ignored "The selected BioSample external identifier was authoritative over duplicate sample identifiers."
                else
                    match relative with
                    | "Accession"
                    | "Refname"
                    | "Refcenter" ->
                        ignored "The BioSample external identifier was authoritative over the SRA sample reference fields."
                    | _ -> None

    let private runFileLocations
        (rawOwner: string)
        (dataBlock: RunDataBlock)
        (path: string)
        =
        if isNull dataBlock then
            None
        else
            let matched = Regex.Match(path, @"^DataBlock\.Files\[(\d+)\]\.(Filename|Filetype|Checksum)$")

            if not matched.Success then
                None
            else
                let index = Int32.Parse matched.Groups.[1].Value

                if index >= dataBlock.Files.Count then
                    None
                else
                    let node, relation = SubObjects.runFile rawOwner dataBlock.Files.[index]

                    subObjectPropertyLocations node relation matched.Groups.[2].Value
                    |> Option.map Emission

    let private analysisFileLocations
        (rawOwner: string)
        (files: Collection<AnalysisFile>)
        (path: string)
        =
        let matched = Regex.Match(path, @"^Files\[(\d+)\]\.(Filename|Filetype|Checksum|ChecksumMethod)$")

        if not matched.Success then
            None
        else
            let index = Int32.Parse matched.Groups.[1].Value

            if index >= files.Count then
                None
            else
                let node, relation = SubObjects.analysisFile rawOwner files.[index]

                subObjectPropertyLocations node relation matched.Groups.[2].Value
                |> Option.map Emission

    let private indexedReferenceLocations
        (owner: Iri)
        (predicate: Iri)
        (count: int)
        (getReference: int -> RefObject)
        (prefix: string)
        (path: string)
        =
        let matched = Regex.Match(path, sprintf @"^%s\[(\d+)\]\." prefix)

        if not matched.Success then
            None
        else
            let index = Int32.Parse matched.Groups.[1].Value

            if index >= count then
                None
            else
                referenceLocations
                    owner
                    predicate
                    (getReference index)
                    (sprintf "%s[%d]" prefix index)
                    path

    let private indexedBioSampleReferenceLocations
        (owner: Iri)
        (count: int)
        (getReference: int -> RefObject)
        (prefix: string)
        (path: string)
        =
        let matched = Regex.Match(path, sprintf @"^%s\[(\d+)\]\." prefix)

        if not matched.Success then
            None
        else
            let index = Int32.Parse matched.Groups.[1].Value

            if index >= count then
                None
            else
                bioSampleReferenceLocations
                    owner
                    (getReference index)
                    (sprintf "%s[%d]" prefix index)
                    path

    let private submissionContactLocations
        (rawOwner: string)
        (contacts: Collection<SubmissionContactsContact>)
        (entry: XPathEntry)
        =
        let matched = Regex.Match(entry.Path, @"^Contacts\[(\d+)\]\.(Name|InformOnStatus|InformOnError)$")

        if not matched.Success then
            None
        else
            let index = Int32.Parse matched.Groups.[1].Value

            if index >= contacts.Count then
                None
            elif String.IsNullOrWhiteSpace contacts.[index].Name then
                ignored "A submission contact without a name cannot receive a stable ArcIR agent identity."
            else
                SubObjects.submissionContact rawOwner contacts.[index]
                |> Option.bind (fun (node, relation) ->
                    let propertyName = matched.Groups.[2].Value

                    if String.IsNullOrWhiteSpace entry.Value then
                        ignored "The present XML field was blank, and the converter intentionally omits blank string properties."
                    else
                        subObjectPropertyLocations node relation propertyName
                        |> Option.map Emission)

    let private receiptRawId (receipt: Receipt) =
        let bySubmission = if isNull receipt.Submission then null else receipt.Submission.Accession

        [ receipt.SubmissionFile; bySubmission ]
        |> List.tryFind (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue "receipt"

    let private receiptBucketLocations
        (owner: Iri)
        (receipt: Receipt)
        (path: string)
        =
        let buckets =
            [ "Analysis", receipt.Analysis
              "Experiment", receipt.Experiment
              "Run", receipt.Run
              "Sample", receipt.Sample
              "Study", receipt.Study
              "Project", receipt.Project
              "Dataset", receipt.Dataset
              "Policy", receipt.Policy
              "Dac", receipt.Dac
              "Checklist", receipt.Checklist
              "Samplegroup", receipt.Samplegroup ]

        let matched =
            Regex.Match(
                path,
                @"^(Analysis|Experiment|Run|Sample|Study|Project|Dataset|Policy|Dac|Checklist|Samplegroup)\[(\d+)\]\.(.+)$"
            )

        if not matched.Success then
            None
        else
            let bucket = buckets |> List.find (fun (name, _) -> name = matched.Groups.[1].Value) |> snd
            let index = Int32.Parse matched.Groups.[2].Value

            if index >= bucket.Count then
                None
            else
                let id = bucket.[index]

                match matched.Groups.[3].Value with
                | "Accession" when not (String.IsNullOrWhiteSpace id.Accession) ->
                    emitted
                        [ relationLocation owner Vocabulary.Rel.acknowledges (Identity.objectId id.Accession) ]
                | "Alias"
                | "StatusValue"
                | "HoldUntilDateValue" ->
                    ignored "Receipt acknowledgement targets are resolved from archive accessions; auxiliary receipt identity metadata is not emitted."
                | value when value.StartsWith("ExtId[", StringComparison.Ordinal) ->
                    ignored "Receipt acknowledgement targets are resolved from archive accessions; auxiliary receipt identity metadata is not emitted."
                | _ -> None

    let private receiptSubmissionLocations (owner: Iri) (receipt: Receipt) (path: string) =
        if isNull receipt.Submission || not (path.StartsWith("Submission.", StringComparison.Ordinal)) then
            None
        else
            match path.Substring("Submission.".Length) with
            | "Accession" when not (String.IsNullOrWhiteSpace receipt.Submission.Accession) ->
                let outputs =
                    [ relationLocation
                          owner
                          Vocabulary.Rel.acknowledges
                          (Identity.objectId receipt.Submission.Accession)
                      if String.IsNullOrWhiteSpace receipt.SubmissionFile then
                          ArcJsonLocation.Object owner ]

                emitted outputs
            | "Alias"
            | "StatusValue"
            | "HoldUntilDateValue" ->
                ignored "Receipt acknowledgement targets are resolved from archive accessions; auxiliary submission metadata is not emitted."
            | value when value.StartsWith("ExtId[", StringComparison.Ordinal) ->
                ignored "Receipt acknowledgement targets are resolved from archive accessions; external submission identifiers are not emitted."
            | _ -> None

    let private genericStringClassification
        (owner: Iri)
        (source: string)
        (key: string)
        (value: string)
        =
        if String.IsNullOrWhiteSpace value then
            ignored "The present XML field was blank, and the converter intentionally omits blank string assertions."
        else
            emitted (genericLocations owner source key)

    let private genericInstitutionClassification
        (rawOwner: string)
        (owner: Iri)
        (source: string)
        (key: string)
        (value: string)
        =
        match genericStringClassification owner source key value with
        | Some(Emission outputs) ->
            let institutionOutputs = institutionLocations rawOwner value |> Option.defaultValue []
            emitted (outputs @ institutionOutputs)
        | outcome -> outcome

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

    let private classifyBioSample (sample: BioSample) (owner: Iri) (entry: XPathEntry) =
        let rawOwner = Convert.entityId sample

        let scalar =
            match entry.Path with
            | "Title" -> genericStringClassification owner bioSampleSource "Title" entry.Value
            | "Description" -> genericStringClassification owner bioSampleSource "Description" entry.Value
            | "CenterName" -> institutionLocations rawOwner sample.CenterName |> Option.map Emission
            | "BrokerName" -> institutionLocations rawOwner sample.BrokerName |> Option.map Emission
            | _ -> None

        scalar
        |> Option.orElseWith (fun () -> identityLocations owner sample.Accession entry.Path)
        |> Option.orElseWith (fun () ->
            identifierLocations owner sample.Identifiers entry.Path |> Option.map Emission)
        |> Option.orElseWith (fun () ->
            attributeLocations owner sample.SampleAttributes entry.Path "SampleAttributes"
            |> Option.map Emission)
        |> Option.orElseWith (fun () -> organismLocations rawOwner sample.SampleName entry.Path)

    let private classifyExperiment (experiment: Experiment) (owner: Iri) (entry: XPathEntry) =
        let rawOwner = Convert.entityId experiment

        let scalar =
            match entry.Path with
            | "Title" -> genericStringClassification owner experimentSource "Title" entry.Value
            | "Design.DesignDescription" ->
                genericStringClassification owner experimentSource "DesignDescription" entry.Value
            | "CenterName" -> institutionLocations rawOwner experiment.CenterName |> Option.map Emission
            | "BrokerName" -> institutionLocations rawOwner experiment.BrokerName |> Option.map Emission
            | _ -> None

        scalar
        |> Option.orElseWith (fun () -> identityLocations owner experiment.Accession entry.Path)
        |> Option.orElseWith (fun () ->
            identifierLocations owner experiment.Identifiers entry.Path |> Option.map Emission)
        |> Option.orElseWith (fun () ->
            attributeLocations owner experiment.ExperimentAttributes entry.Path "ExperimentAttributes"
            |> Option.map Emission)
        |> Option.orElseWith (fun () ->
            if isNull experiment.StudyRef then
                None
            else
                referenceLocations
                    owner
                    Vocabulary.Rel.hasStudy
                    experiment.StudyRef
                    "StudyRef"
                    entry.Path)
        |> Option.orElseWith (fun () ->
            match experiment.Design with
            | null -> None
            | design ->
                bioSampleReferenceLocations
                    owner
                    design.SampleDescriptor
                    "Design.SampleDescriptor"
                    entry.Path)
        |> Option.orElseWith (fun () ->
            match experiment.Design with
            | null -> None
            | design -> protocolLocations rawOwner design.LibraryDescriptor entry)
        |> Option.orElseWith (fun () -> instrumentLocations rawOwner experiment.Platform entry.Path)

    let private classifyRun (run: Run) (owner: Iri) (entry: XPathEntry) =
        let rawOwner = Convert.entityId run

        let scalar =
            match entry.Path with
            | "Title" -> genericStringClassification owner runSource "Title" entry.Value
            | "RunCenter" -> genericStringClassification owner runSource "RunCenter" entry.Value
            | "RunDateValue" -> emitted (genericLocations owner runSource "RunDate")
            | "CenterName" -> institutionLocations rawOwner run.CenterName |> Option.map Emission
            | "BrokerName" -> institutionLocations rawOwner run.BrokerName |> Option.map Emission
            | _ -> None

        scalar
        |> Option.orElseWith (fun () -> identityLocations owner run.Accession entry.Path)
        |> Option.orElseWith (fun () ->
            identifierLocations owner run.Identifiers entry.Path |> Option.map Emission)
        |> Option.orElseWith (fun () ->
            attributeLocations owner run.RunAttributes entry.Path "RunAttributes"
            |> Option.map Emission)
        |> Option.orElseWith (fun () ->
            if isNull run.ExperimentRef then
                None
            else
                referenceLocations
                    owner
                    Vocabulary.Rel.hasExperiment
                    run.ExperimentRef
                    "ExperimentRef"
                    entry.Path)
        |> Option.orElseWith (fun () -> instrumentLocations rawOwner run.Platform entry.Path)
        |> Option.orElseWith (fun () -> runFileLocations rawOwner run.DataBlock entry.Path)

    let private classifyAnalysis (analysis: Analysis) (owner: Iri) (entry: XPathEntry) =
        let rawOwner = Convert.entityId analysis

        let scalar =
            match entry.Path with
            | "Title" -> genericStringClassification owner analysisSource "Title" entry.Value
            | "Description" -> genericStringClassification owner analysisSource "Description" entry.Value
            | "AnalysisCenter" ->
                genericInstitutionClassification rawOwner owner analysisSource "AnalysisCenter" entry.Value
            | "AnalysisDateValue" -> emitted (genericLocations owner analysisSource "AnalysisDate")
            | "CenterName" -> institutionLocations rawOwner analysis.CenterName |> Option.map Emission
            | "BrokerName" -> institutionLocations rawOwner analysis.BrokerName |> Option.map Emission
            | _ -> None

        scalar
        |> Option.orElseWith (fun () -> identityLocations owner analysis.Accession entry.Path)
        |> Option.orElseWith (fun () ->
            identifierLocations owner analysis.Identifiers entry.Path |> Option.map Emission)
        |> Option.orElseWith (fun () ->
            attributeLocations owner analysis.AnalysisAttributes entry.Path "AnalysisAttributes"
            |> Option.map Emission)
        |> Option.orElseWith (fun () ->
            if isNull analysis.StudyRef then
                None
            else
                referenceLocations owner Vocabulary.Rel.hasStudy analysis.StudyRef "StudyRef" entry.Path)
        |> Option.orElseWith (fun () ->
            indexedBioSampleReferenceLocations
                owner
                analysis.SampleRef.Count
                (fun index -> analysis.SampleRef.[index])
                "SampleRef"
                entry.Path)
        |> Option.orElseWith (fun () ->
            indexedReferenceLocations
                owner
                Vocabulary.Rel.hasExperiment
                analysis.ExperimentRef.Count
                (fun index -> analysis.ExperimentRef.[index])
                "ExperimentRef"
                entry.Path)
        |> Option.orElseWith (fun () ->
            indexedReferenceLocations
                owner
                Vocabulary.Rel.hasRun
                analysis.RunRef.Count
                (fun index -> analysis.RunRef.[index])
                "RunRef"
                entry.Path)
        |> Option.orElseWith (fun () ->
            indexedReferenceLocations
                owner
                Vocabulary.Rel.hasAnalysis
                analysis.AnalysisRef.Count
                (fun index -> analysis.AnalysisRef.[index])
                "AnalysisRef"
                entry.Path)
        |> Option.orElseWith (fun () -> analysisFileLocations rawOwner analysis.Files entry.Path)

    let private classifySubmission (submission: Submission) (owner: Iri) (entry: XPathEntry) =
        let rawOwner = Convert.entityId submission

        let scalar =
            match entry.Path with
            | "Title" -> genericStringClassification owner submissionSource "Title" entry.Value
            | "SubmissionComment" ->
                genericStringClassification owner submissionSource "SubmissionComment" entry.Value
            | "LabName" ->
                genericInstitutionClassification rawOwner owner submissionSource "LabName" entry.Value
            | "SubmissionDateValue" -> emitted (genericLocations owner submissionSource "SubmissionDate")
            | "CenterName" -> institutionLocations rawOwner submission.CenterName |> Option.map Emission
            | "BrokerName" -> institutionLocations rawOwner submission.BrokerName |> Option.map Emission
            | _ -> None

        scalar
        |> Option.orElseWith (fun () -> identityLocations owner submission.Accession entry.Path)
        |> Option.orElseWith (fun () ->
            identifierLocations owner submission.Identifiers entry.Path |> Option.map Emission)
        |> Option.orElseWith (fun () ->
            attributeLocations owner submission.SubmissionAttributes entry.Path "SubmissionAttributes"
            |> Option.map Emission)
        |> Option.orElseWith (fun () ->
            submissionContactLocations rawOwner submission.Contacts entry)

    let private classifyReceipt (receipt: Receipt) (owner: Iri) (entry: XPathEntry) =
        let scalar =
            match entry.Path with
            | "Success" -> emitted (genericLocations owner receiptSource "Success")
            | "ReceiptDate" -> emitted (genericLocations owner receiptSource "ReceiptDate")
            | "SubmissionFile" when String.IsNullOrWhiteSpace entry.Value ->
                ignored "A blank submission-file attribute neither identifies nor annotates the receipt."
            | "SubmissionFile" ->
                emitted (ArcJsonLocation.Object owner :: genericLocations owner receiptSource "SubmissionFile")
            | _ -> None

        scalar
        |> Option.orElseWith (fun () -> receiptSubmissionLocations owner receipt entry.Path)
        |> Option.orElseWith (fun () -> receiptBucketLocations owner receipt entry.Path)

    let private account
        (entity: string)
        (artifact: ArtifactRevision)
        (classify: XPathEntry -> FieldClassification option)
        (entries: seq<XPathEntry>)
        =
        let diagnostics = ResizeArray<Diagnostic>()

        let accountingEntries =
            entries
            |> Seq.map (fun entry ->
                let input = sourceRef artifact entry

                match classify entry with
                | Some(Emission outputs) ->
                    { RuleId = ruleId entity "emit" entry.Path
                      Input = input
                      Outcome = FieldAccountingOutcome.Emitted outputs }
                | Some(IntentionalOmission reason) ->
                    { RuleId = ruleId entity "ignore" entry.Path
                      Input = input
                      Outcome = FieldAccountingOutcome.Ignored reason }
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
            |> account
                "bioproject"
                artifact
                (fun entry -> classifyBioProject project owner entry |> Option.map Emission) }

    /// Converts and accounts for every present leaf in one Study source artifact.
    let study (artifact: ArtifactRevision) (study: Study) =
        let conversion = StudyConversion.convert study
        let owner = Identity.objectId (Convert.entityId study)

        { Conversion = conversion
          Accounting =
            BioFSharp.IO.INSDC.Study.xpathEntries study
            |> account
                "study"
                artifact
                (fun entry -> classifyStudy study owner entry |> Option.map Emission) }

    /// Converts and accounts for every present leaf in one BioSample source artifact.
    let bioSample (artifact: ArtifactRevision) (sample: BioSample) =
        let conversion = BioSampleConversion.convert sample
        let owner = Identity.objectId (Convert.entityId sample)

        { Conversion = conversion
          Accounting =
            BioFSharp.IO.INSDC.BioSample.xpathEntries sample
            |> account "biosample" artifact (classifyBioSample sample owner) }

    /// Converts and accounts for every present leaf in one Experiment source artifact.
    let experiment (artifact: ArtifactRevision) (experiment: Experiment) =
        let conversion = ExperimentConversion.convert experiment
        let owner = Identity.objectId (Convert.entityId experiment)

        { Conversion = conversion
          Accounting =
            BioFSharp.IO.INSDC.Experiment.xpathEntries experiment
            |> account "experiment" artifact (classifyExperiment experiment owner) }

    /// Converts and accounts for every present leaf in one Run source artifact.
    let run (artifact: ArtifactRevision) (run: Run) =
        let conversion = RunConversion.convert run
        let owner = Identity.objectId (Convert.entityId run)

        { Conversion = conversion
          Accounting =
            BioFSharp.IO.INSDC.Run.xpathEntries run
            |> account "run" artifact (classifyRun run owner) }

    /// Converts and accounts for every present leaf in one Analysis source artifact.
    let analysis (artifact: ArtifactRevision) (analysis: Analysis) =
        let conversion = AnalysisConversion.convert analysis
        let owner = Identity.objectId (Convert.entityId analysis)

        { Conversion = conversion
          Accounting =
            BioFSharp.IO.INSDC.Analysis.xpathEntries analysis
            |> account "analysis" artifact (classifyAnalysis analysis owner) }

    /// Converts and accounts for every present leaf in one Submission source artifact.
    let submission (artifact: ArtifactRevision) (submission: Submission) =
        let conversion = SubmissionConversion.convert submission
        let owner = Identity.objectId (Convert.entityId submission)

        { Conversion = conversion
          Accounting =
            BioFSharp.IO.INSDC.Submission.xpathEntries submission
            |> account "submission" artifact (classifySubmission submission owner) }

    /// Converts and accounts for every present leaf in one Receipt source artifact.
    let receipt (artifact: ArtifactRevision) (receipt: Receipt) =
        let conversion = ReceiptConversion.convert receipt
        let owner = Identity.objectId (receiptRawId receipt)

        { Conversion = conversion
          Accounting =
            BioFSharp.IO.INSDC.Receipt.xpathEntries receipt
            |> account "receipt" artifact (classifyReceipt receipt owner) }
