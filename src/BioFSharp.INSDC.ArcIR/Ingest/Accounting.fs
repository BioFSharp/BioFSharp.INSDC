namespace BioFSharp.INSDC.ArcIR

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Xml.Linq
open BioFSharp.ArcIR

type private SupplementaryOccurrence =
    {
        RuleKey: string
        Selector: FragmentSelector
        Value: string
    }

type private JatsOccurrence =
    {
        Source: SupplementaryOccurrence
        Element: XElement
        Attribute: XAttribute option
    }

type private SupplementaryClassification =
    | SupplementaryEmission of ArcJsonLocation list
    | SupplementaryOmission of string

/// Occurrence-level F1 accounting for supplementary JATS and count-table artifacts.
[<RequireQualifiedAccess>]
module IngestAccounting =

    /// W3C Selectors and States, used to designate count-header cells by exact byte range.
    let DataPositionSelectorConformsTo = Iri.Create "https://www.w3.org/TR/selectors-states/"

    [<Literal>]
    let private paperSource = "paper"

    let private sha256 (value: string) =
        use algorithm = SHA256.Create()

        algorithm.ComputeHash(Encoding.UTF8.GetBytes value)
        |> Array.map (fun (byte: byte) -> byte.ToString("x2"))
        |> String.concat ""

    let private ruleId source outcome ruleKey =
        Iri.Create(
            sprintf
                "urn:biofsharp:insdc:f1:rule:%s:%s:%s"
                source
                outcome
                (sha256 ruleKey)
        )

    let private reportId source (artifact: ArtifactRevision) =
        Iri.Create(
            sprintf
                "urn:biofsharp:insdc:f1:diagnostic-report:%s:%s"
                source
                (sha256 (artifact.Path + "\n" + artifact.Sha256))
        )

    let private sourceRef artifact occurrence =
        { Artifact = artifact; Selector = occurrence.Selector }

    let private unsupportedDiagnostic source artifact occurrence =
        let input = sourceRef artifact occurrence

        { Id =
            Iri.Create(
                sprintf
                    "urn:biofsharp:arcir:diagnostic:f1:%s"
                    (sha256 (source + "\n" + artifact.Sha256 + "\n" + occurrence.Selector.Value))
            )
          Code = Iri.Create "urn:biofsharp:arcir:diagnostic-code:unsupported-source-field"
          Severity = DiagnosticSeverity.Warning
          Message =
            sprintf
                "The %s F1 converter does not yet emit source occurrence '%s'."
                source
                occurrence.Selector.Value
          Targets = [ input ]
          Related = [] }

    let private account source artifact classify occurrences =
        let diagnostics = ResizeArray<Diagnostic>()

        let entries =
            occurrences
            |> Seq.map (fun occurrence ->
                let input = sourceRef artifact occurrence

                match classify occurrence with
                | Some(SupplementaryEmission outputs) ->
                    { RuleId = ruleId source "emit" occurrence.RuleKey
                      Input = input
                      Outcome = FieldAccountingOutcome.Emitted outputs }
                | Some(SupplementaryOmission reason) ->
                    { RuleId = ruleId source "ignore" occurrence.RuleKey
                      Input = input
                      Outcome = FieldAccountingOutcome.Ignored reason }
                | None ->
                    let diagnostic = unsupportedDiagnostic source artifact occurrence
                    diagnostics.Add diagnostic

                    { RuleId = ruleId source "unsupported" occurrence.RuleKey
                      Input = input
                      Outcome = FieldAccountingOutcome.Unsupported diagnostic.Id })
            |> List.ofSeq

        diagnostics
        |> DiagnosticReport.create (reportId source artifact)
        |> fun diagnosticReport -> FieldAccounting.create diagnosticReport entries

    let private ensureArtifactMatches artifact bytes =
        if not (ArtifactRevision.verifyBytes artifact bytes) then
            invalidArg (nameof bytes) "The supplied bytes do not match the declared artifact revision."

    let private annotationLocations (owner: Iri) (term: Iri) =
        let assertionId = Identity.assertion owner "annotation" term.Value

        [ ArcJsonLocation.ObjectAnnotation(owner, assertionId)
          ArcJsonLocation.ObjectAnnotationValue(owner, assertionId) ]

    let private paperAnnotationLocations owner key =
        (Annotations.field paperSource key (ArcValue.String "")).Id
        |> annotationLocations owner

    let private propertyLocations (node: ArcObject) propertyName =
        let predicate = Vocabulary.Property.ofName propertyName

        node.Properties.Values
        |> Seq.tryFind (fun property -> property.Predicate = predicate)
        |> Option.map (fun property -> [ ArcJsonLocation.Object node.Id; ArcJsonLocation.PropertyValue(node.Id, property.Id) ])

    let private authorLocations paperId author propertyName =
        Paper.authorFragment paperId author
        |> Option.bind (fun (node, relation) ->
            propertyLocations node propertyName
            |> Option.map (fun locations -> locations @ [ ArcJsonLocation.Relation relation.Id ]))

    let private authorIdentityLocations paperId author =
        Paper.authorFragment paperId author
        |> Option.map (fun (node, relation) -> [ ArcJsonLocation.Object node.Id; ArcJsonLocation.Relation relation.Id ])

    let private localName (element: XElement) = element.Name.LocalName

    let private attributeValue name (element: XElement) =
        element.Attributes()
        |> Seq.tryPick (fun attribute ->
            if String.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal) then
                Some attribute.Value
            else
                None)

    let private attributeEquals name value element =
        attributeValue name element
        |> Option.exists (fun actual -> String.Equals(actual, value, StringComparison.OrdinalIgnoreCase))

    let private elementStep (element: XElement) =
        let namedStep = sprintf "*[local-name()='%s']" element.Name.LocalName

        match element.Parent with
        | null -> namedStep
        | parent ->
            let siblings =
                parent.Elements()
                |> Seq.filter (fun sibling -> sibling.Name.LocalName = element.Name.LocalName)
                |> List.ofSeq

            if siblings.Length = 1 then
                namedStep
            else
                let index =
                    siblings
                    |> List.findIndex (fun sibling -> Object.ReferenceEquals(sibling, element))
                    |> ((+) 1)

                sprintf "%s[%d]" namedStep index

    let private elementXPath (element: XElement) =
        element.AncestorsAndSelf()
        |> Seq.rev
        |> Seq.map elementStep
        |> String.concat "/"
        |> (+) "/"

    let private jatsOccurrences (document: XDocument) =
        let root = document.Root

        if isNull root then
            []
        else
            let rootAttributes = root.Attributes() |> Seq.filter (fun attribute -> not attribute.IsNamespaceDeclaration)

            let metadataElements =
                root.DescendantsAndSelf()
                |> Seq.tryFind (fun element -> element.Name.LocalName = "front")
                |> Option.map (fun front -> front.DescendantsAndSelf())
                |> Option.defaultValue Seq.empty
                |> List.ofSeq

            let attributeOccurrences =
                seq {
                    yield! rootAttributes

                    for element in metadataElements do
                        yield! element.Attributes() |> Seq.filter (fun attribute -> not attribute.IsNamespaceDeclaration)
                }
                |> Seq.distinctBy (fun attribute -> elementXPath attribute.Parent + "/@" + attribute.Name.LocalName)
                |> Seq.map (fun attribute ->
                    let xpath = elementXPath attribute.Parent + "/@*[local-name()='" + attribute.Name.LocalName + "']"

                    { Source =
                        { RuleKey = "attribute:" + xpath
                          Selector =
                            { ConformsTo = F1Accounting.XPointerConformsTo
                              Value = "#xpointer(" + xpath + ")" }
                          Value = attribute.Value }
                      Element = attribute.Parent
                      Attribute = Some attribute })

            let textOccurrences =
                metadataElements
                |> Seq.filter (fun element ->
                    not element.HasElements
                    && not (String.IsNullOrWhiteSpace element.Value))
                |> Seq.map (fun element ->
                    let xpath = elementXPath element

                    { Source =
                        { RuleKey = "element:" + xpath
                          Selector =
                            { ConformsTo = F1Accounting.XPointerConformsTo
                              Value = "#xpointer(" + xpath + ")" }
                          Value = element.Value.Trim() }
                      Element = element
                      Attribute = None })

            Seq.append attributeOccurrences textOccurrences
            |> Seq.sortBy (fun occurrence -> occurrence.Source.Selector.Value)
            |> List.ofSeq

    let private authorContributions (document: XDocument) =
        document.Descendants()
        |> Seq.filter (fun element ->
            element.Name.LocalName = "contrib"
            && attributeEquals "contrib-type" "author" element)
        |> List.ofSeq

    let private tryAuthorIndex authors (element: XElement) =
        element.AncestorsAndSelf()
        |> Seq.tryFind (fun ancestor -> ancestor.Name.LocalName = "contrib")
        |> Option.bind (fun contribution ->
            authors
            |> List.tryFindIndex (fun author -> Object.ReferenceEquals(author, contribution)))

    let private tryContainer containerName (element: XElement) =
        element.AncestorsAndSelf()
        |> Seq.tryFind (fun candidate -> candidate.Name.LocalName = containerName)

    let private classifyJats document meta paperId (occurrence: JatsOccurrence) =
        let element = occurrence.Element
        let name = localName element
        let authors = authorContributions document

        let authorProperty propertyName =
            tryAuthorIndex authors element
            |> Option.bind (fun index -> meta.Authors |> List.tryItem index)
            |> Option.bind (fun author -> authorLocations paperId author propertyName)
            |> Option.map SupplementaryEmission

        let authorIdentity () =
            tryAuthorIndex authors element
            |> Option.bind (fun index -> meta.Authors |> List.tryItem index)
            |> Option.bind (authorIdentityLocations paperId)
            |> Option.map SupplementaryEmission

        let referencedAffiliationLocations (affiliation: XElement) =
            match attributeValue "id" affiliation with
            | None -> []
            | Some affiliationId ->
                authors
                |> List.mapi (fun index authorElement -> index, authorElement)
                |> List.choose (fun (index, authorElement) ->
                    let referencesId =
                        authorElement.Descendants()
                        |> Seq.exists (fun candidate ->
                            candidate.Name.LocalName = "xref"
                            && attributeEquals "ref-type" "aff" candidate
                            && attributeEquals "rid" affiliationId candidate)

                    if referencesId then
                        meta.Authors
                        |> List.tryItem index
                        |> Option.bind (fun author -> authorLocations paperId author "Affiliation")
                    else
                        None)
                |> List.collect id

        match occurrence.Attribute with
        | Some attribute ->
            match name, attribute.Name.LocalName with
            | "article", "article-type" ->
                Some(SupplementaryOmission "The JATS article type is retained in the source artifact but is not an ArcIR paper field.")
            | "article-id", "pub-id-type" when attributeEquals "pub-id-type" "doi" element ->
                Some(SupplementaryEmission(ArcJsonLocation.Object(Identity.objectId paperId) :: paperAnnotationLocations (Identity.objectId paperId) "DOI"))
            | "article-id", "pub-id-type" ->
                Some(SupplementaryOmission "Only the DOI article identifier is projected by the current paper converter.")
            | "contrib", "contrib-type" when attributeEquals "contrib-type" "author" element -> authorIdentity ()
            | "contrib-id", "contrib-id-type" when attributeEquals "contrib-id-type" "orcid" element ->
                authorProperty "Orcid"
            | "xref", ("ref-type" | "rid") when attributeEquals "ref-type" "aff" element ->
                authorProperty "Affiliation"
            | "aff", "id" ->
                match referencedAffiliationLocations element with
                | [] -> None
                | outputs -> Some(SupplementaryEmission outputs)
            | _ -> None
        | None ->
            match name with
            | "article-title" ->
                Some(SupplementaryEmission(paperAnnotationLocations (Identity.objectId paperId) "Title"))
            | "journal-title" ->
                Some(SupplementaryEmission(paperAnnotationLocations (Identity.objectId paperId) "Journal"))
            | "article-id" when attributeEquals "pub-id-type" "doi" element ->
                Some(SupplementaryEmission(ArcJsonLocation.Object(Identity.objectId paperId) :: paperAnnotationLocations (Identity.objectId paperId) "DOI"))
            | "article-id" ->
                Some(SupplementaryOmission "Only the DOI article identifier is projected by the current paper converter.")
            | "given-names"
            | "surname"
            | "string-name" -> authorProperty "Name"
            | "email" -> authorProperty "Email"
            | "contrib-id" when attributeEquals "contrib-id-type" "orcid" element -> authorProperty "Orcid"
            | "aff" when tryAuthorIndex authors element |> Option.isSome -> authorProperty "Affiliation"
            | "aff" ->
                match referencedAffiliationLocations element with
                | [] -> None
                | outputs -> Some(SupplementaryEmission outputs)
            | _ ->
                tryContainer "article-title" element
                |> Option.map (fun _ ->
                    SupplementaryEmission(paperAnnotationLocations (Identity.objectId paperId) "Title"))
                |> Option.orElseWith (fun () ->
                    tryContainer "journal-title" element
                    |> Option.map (fun _ ->
                        SupplementaryEmission(paperAnnotationLocations (Identity.objectId paperId) "Journal")))
                |> Option.orElseWith (fun () ->
                    tryContainer "article-id" element
                    |> Option.bind (fun articleId ->
                        if attributeEquals "pub-id-type" "doi" articleId then
                            Some(
                                SupplementaryEmission(
                                    ArcJsonLocation.Object(Identity.objectId paperId)
                                    :: paperAnnotationLocations (Identity.objectId paperId) "DOI"
                                )
                            )
                        else
                            Some(
                                SupplementaryOmission
                                    "Only the DOI article identifier is projected by the current paper converter."
                            )))
                |> Option.orElseWith (fun () ->
                    [ "given-names", "Name"
                      "surname", "Name"
                      "string-name", "Name"
                      "email", "Email"
                      "contrib-id", "Orcid" ]
                    |> List.tryPick (fun (containerName, propertyName) ->
                        tryContainer containerName element
                        |> Option.bind (fun _ -> authorProperty propertyName)))
                |> Option.orElseWith (fun () ->
                    tryContainer "aff" element
                    |> Option.bind (fun affiliation ->
                        if tryAuthorIndex authors affiliation |> Option.isSome then
                            authorProperty "Affiliation"
                        else
                            match referencedAffiliationLocations affiliation with
                            | [] -> None
                            | outputs -> Some(SupplementaryEmission outputs)))

    let private countOccurrences (bytes: byte array) =
        let headerStart =
            if bytes.Length >= 3 && bytes.[0] = 0xefuy && bytes.[1] = 0xbbuy && bytes.[2] = 0xbfuy then
                3
            else
                0

        let headerEnd =
            bytes
            |> Array.tryFindIndex (fun value -> value = 0x0auy || value = 0x0duy)
            |> Option.defaultValue bytes.Length

        if headerEnd = headerStart then
            []
        else
            let delimiter =
                bytes
                |> Seq.skip headerStart
                |> Seq.take (headerEnd - headerStart)
                |> Seq.tryFind (fun value -> value = 0x09uy)
                |> Option.map (fun _ -> 0x09uy)
                |> Option.defaultValue 0x2cuy

            let separators =
                seq { headerStart .. headerEnd - 1 }
                |> Seq.filter (fun index -> bytes.[index] = delimiter)
                |> List.ofSeq

            let starts = headerStart :: (separators |> List.map ((+) 1))
            let ends = separators @ [ headerEnd ]

            List.zip starts ends
            |> List.mapi (fun zeroBasedIndex (startPosition, endPosition) ->
                let index = zeroBasedIndex + 1

                { RuleKey = sprintf "header[%d]" index
                  Selector =
                    { ConformsTo = DataPositionSelectorConformsTo
                      Value =
                        sprintf
                            "#selector(type=DataPositionSelector,start=%d,end=%d)"
                            startPosition
                            endPosition }
                  Value = Encoding.UTF8.GetString(bytes, startPosition, endPosition - startPosition) })

    let private countColumnLocations fileId (column: CountColumn) =
        let rawColumnId = sprintf "%s#col=%d" fileId column.Index
        let columnId = Identity.objectId rawColumnId
        let columnNode =
            GraphBuilder.object'
                rawColumnId
                ArcObjectKind.Resource
                [ Vocabulary.DType.countColumn ]
                [ Vocabulary.Property.ofName "Column", ArcValue.Integer(int64 column.Index)
                  Vocabulary.Property.ofName "RunAccession", ArcValue.String column.RunAccession
                  Vocabulary.Property.ofName "FragmentSelector", ArcValue.String(sprintf "#col=%d" column.Index) ]
                []

        let properties =
            [ "Column"; "RunAccession"; "FragmentSelector" ]
            |> List.choose (fun propertyName ->
                propertyLocations columnNode propertyName
                |> Option.bind (List.tryFind (function ArcJsonLocation.PropertyValue _ -> true | _ -> false)))

        let hasColumn =
            Identity.relation (Identity.objectId fileId) Vocabulary.Rel.hasColumn columnId
            |> ArcJsonLocation.Relation

        let producesData =
            Identity.relation (Identity.objectId column.RunAccession) Vocabulary.Rel.producesData columnId
            |> ArcJsonLocation.Relation

        ArcJsonLocation.Object columnId :: properties @ [ hasColumn; producesData ]

    /// Converts a JATS artifact from its exact bytes and accounts for every front-matter leaf inspected by F1.
    let paper
        (artifact: ArtifactRevision)
        (fileName: string)
        (bytes: byte array)
        (relatedAccessions: string list)
        : AccountedConversion =
        ensureArtifactMatches artifact bytes

        let metadata = IngestReaders.readJatsBytes bytes
        let file = IngestReaders.describeBytes fileName bytes
        let conversion = Paper.convert metadata file relatedAccessions
        let rawPaperId = Paper.paperId metadata file
        use stream = new MemoryStream(bytes, false)
        let document = XDocument.Load stream
        let occurrences = jatsOccurrences document

        { Conversion = conversion
          Accounting =
            occurrences
            |> Seq.map _.Source
            |> account
                "paper"
                artifact
                (fun source ->
                    occurrences
                    |> List.find (fun candidate -> candidate.Source.Selector = source.Selector)
                    |> classifyJats document metadata rawPaperId) }

    /// Converts a delimited count-table artifact from its exact bytes and accounts for every header cell inspected by F1.
    let countData
        (artifact: ArtifactRevision)
        (fileName: string)
        (bytes: byte array)
        : AccountedConversion =
        ensureArtifactMatches artifact bytes

        let countFile = IngestReaders.readCountBytes fileName bytes
        let conversion = CountData.convert countFile
        let fileId = CountData.fileId countFile.File

        let classify occurrence =
            countFile.Columns
            |> List.tryFind (fun column ->
                occurrence.RuleKey = sprintf "header[%d]" column.Index
                && occurrence.Value.Trim() = column.RunAccession)
            |> Option.map (countColumnLocations fileId >> SupplementaryEmission)
            |> Option.orElseWith (fun () ->
                Some(
                    SupplementaryOmission
                        "The header cell does not identify an INSDC run and is outside the header-only count-linkage scope."
                ))

        { Conversion = conversion
          Accounting = countOccurrences bytes |> account "count-data" artifact classify }
