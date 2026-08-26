namespace BioFSharp.INSDC.Tests

open System
open System.Collections
open System.IO
open System.Reflection
open System.Xml.Linq
open Xunit

open OBO.NET

open BioFSharp.FileFormats.INSDC
open BioFSharp.IO.INSDC

open BioFSharp.ArcIR
open BioFSharp.INSDC.ArcIR

module private ArcTest =

    let id = Identity.objectId

    let hasType term (object': ArcObject) =
        object'.Types.Values |> Seq.exists (fun assertion -> assertion.Term = term)

    let propertyValue name (object': ArcObject) =
        let predicate = Vocabulary.Property.ofName name
        object'.Properties.Values
        |> Seq.find (fun property -> property.Predicate = predicate)
        |> fun property -> property.Value

    let hasProperty name (object': ArcObject) =
        let predicate = Vocabulary.Property.ofName name
        object'.Properties.Values |> Seq.exists (fun property -> property.Predicate = predicate)

    let localName (iri: Iri) =
        let value = iri.Value
        let cut = max (value.LastIndexOf '#') (value.LastIndexOf '/')
        if cut >= 0 && cut + 1 < value.Length then Uri.UnescapeDataString(value.Substring(cut + 1)) else value

    let annotationName (ir: ArcIR) (annotation: ArcAnnotation) =
        ir.Terms
        |> Map.tryFind annotation.Property
        |> Option.bind (fun term -> term.Name)
        |> Option.defaultValue (localName annotation.Property)

type ArcMappingTests() =

    // Phase 5: map the 8 IO-readable INSDC entities into the ArcIR property graph. Typed converters build
    // the structure (typed values, sub-objects, edges via the controlled Vocabulary); the structural-
    // ontology decompilation supplies the annotation overlay.
    let read reader file = reader (TestFiles.fixture file) |> Seq.exactlyOne
    let project = read BioProject.read "PRJDB5192.xml"
    let study = read Study.read "DRP003416.xml"
    let sample = read BioSample.read "SAMD00064197.xml"
    let experiment = read Experiment.read "DRX066772.xml"
    let run = read Run.read "DRR072834.xml"
    let analysis = read Analysis.read "ERZ496533.xml"
    let submission = read Submission.read "DRA005154.xml"
    let receipt = Receipt.read (TestFiles.fixture "receipt-sample.xml")

    let ir =
        [ INSDC.bioProject project
          INSDC.study study
          INSDC.bioSample sample
          INSDC.experiment experiment
          INSDC.run run
          INSDC.analysis analysis
          INSDC.submission submission
          INSDC.receipt receipt ]
        |> INSDC.build

    let objectById (id: string) = ir.Objects.[ArcTest.id id]
    let outgoing (id: string) = ArcIR.outgoing (ArcTest.id id) ir |> List.ofSeq
    let hasEdge subject predicate object' =
        outgoing subject |> List.exists (fun r -> r.Predicate = predicate && r.Object = ArcTest.id object')
    let hasPredicate subject predicate = outgoing subject |> List.exists (fun r -> r.Predicate = predicate)
    let byDType dtype = ir.Objects.Values |> Seq.filter (ArcTest.hasType dtype) |> List.ofSeq

    [<Fact>]
    member _.``every entity maps to a node keyed by its accession, with the right kind`` () =
        Assert.Equal(ArcObjectKind.Collection, (objectById project.Accession).Kind)
        Assert.Equal(ArcObjectKind.Collection, (objectById study.Accession).Kind)
        Assert.Equal(ArcObjectKind.Observable, (objectById sample.Accession).Kind)
        Assert.Equal(ArcObjectKind.Activity, (objectById experiment.Accession).Kind)
        Assert.Equal(ArcObjectKind.Activity, (objectById run.Accession).Kind)
        Assert.Equal(ArcObjectKind.Activity, (objectById analysis.Accession).Kind)
        Assert.Equal(ArcObjectKind.Collection, (objectById submission.Accession).Kind)

    [<Fact>]
    member _.``type assertions and predicates use the controlled vocabulary IRIs`` () =
        Assert.True(ArcTest.hasType Vocabulary.DType.bioProject (objectById project.Accession))
        Assert.True(hasPredicate experiment.Accession Vocabulary.Rel.hasStudy)

    [<Fact>]
    member _.``the sample organism is a deduped taxon node with a typed integer TaxonId`` () =
        // Mapping from the typed objects (not the flat string decompilation) is the point: TAXON_ID lands
        // as an ArcValue.Integer, and the taxon node id is shared across every sample of that organism.
        let organism = objectById "taxon:3702"
        Assert.Equal(ArcValue.Integer 3702L, ArcTest.propertyValue "TaxonId" organism)
        Assert.True(hasEdge sample.Accession Vocabulary.Rel.hasOrganism "taxon:3702")

    [<Fact>]
    member _.``the experiment links to study, sample, instrument and protocol`` () =
        Assert.True(hasEdge experiment.Accession Vocabulary.Rel.hasStudy "DRP003416")
        // The SAMPLE_DESCRIPTOR references the SRA accession DRS039895, but the edge resolves to the
        // BioSample node (SAMD00064197) via the descriptor's EXTERNAL_ID[namespace=BioSample].
        Assert.True(hasEdge experiment.Accession Vocabulary.Rel.hasSample sample.Accession)
        Assert.True(hasPredicate experiment.Accession Vocabulary.Rel.usesInstrument)
        Assert.True(hasPredicate experiment.Accession Vocabulary.Rel.hasProtocol)

    [<Fact>]
    member _.``the run references its experiment`` () =
        Assert.True(hasEdge run.Accession Vocabulary.Rel.hasExperiment experiment.Accession)

    [<Fact>]
    member _.``the analysis references its study and produces data-file resources`` () =
        Assert.True(hasPredicate analysis.Accession Vocabulary.Rel.hasStudy)
        Assert.True(hasPredicate analysis.Accession Vocabulary.Rel.producesData)
        Assert.NotEmpty(byDType Vocabulary.DType.data)

    [<Fact>]
    member _.``the bioproject records a related-project edge`` () =
        Assert.True(hasPredicate project.Accession Vocabulary.Rel.hasParentProject)

    [<Fact>]
    member _.``the receipt acknowledges submitted objects and carries typed Success/ReceiptDate`` () =
        let node = byDType Vocabulary.DType.receipt |> List.exactlyOne
        Assert.True(hasPredicate node.Id.Value Vocabulary.Rel.acknowledges)
        let annValue name =
            node.Annotations.Values
            |> Seq.pick (fun annotation -> if ArcTest.annotationName ir annotation = name then Some annotation.Value else None)
        match annValue "Success" with
        | AnnotationValue.Literal(ArcValue.Boolean _) -> ()
        | v -> Assert.True(false, $"Success should be a Boolean literal, got {v}")
        match annValue "ReceiptDate" with
        | AnnotationValue.Literal(ArcValue.DateTime _) -> ()
        | v -> Assert.True(false, $"ReceiptDate should be a DateTime literal, got {v}")

    [<Fact>]
    member _.``a shared institution collapses to one Agent node referenced by several entities`` () =
        let ddbj = ArcTest.id "org:ddbj"
        Assert.True(ir.Objects.ContainsKey ddbj)
        let referrers = ArcIR.incoming ddbj ir |> Seq.length
        Assert.True(referrers > 1, $"expected the shared org node to be referenced more than once, got {referrers}")

    [<Fact>]
    member _.``a closed-vocabulary enum maps to an ArcValue.Iri`` () =
        let instrument = byDType Vocabulary.DType.instrument |> List.head
        match ArcTest.propertyValue "InstrumentModel" instrument with
        | ArcValue.Iri _ -> ()
        | v -> Assert.True(false, $"InstrumentModel should be an Iri, got {v}")

    [<Fact>]
    member _.``mapped entity nodes are decoupled from the flat decompilation`` () =
        // Every entity now uses an explicit per-accession converter: annotations-first (empty Properties)
        // and no shredded structural-ontology leaves (those are entity-qualified, e.g. "BioSample.Foo.Bar").
        let cases =
            [ project.Accession, "BioProject"
              study.Accession, "Study"
              sample.Accession, "BioSample"
              experiment.Accession, "Experiment"
              run.Accession, "Run"
              analysis.Accession, "Analysis"
              submission.Accession, "Submission" ]

        for accession, prefix in cases do
            let node = objectById accession
            Assert.True(node.Properties.IsEmpty, $"{accession} should have empty Properties")
            Assert.NotEmpty node.Annotations
            Assert.False(
                node.Annotations.Values |> Seq.exists (fun annotation -> (ArcTest.annotationName ir annotation).StartsWith(prefix + ".")),
                $"{accession} should carry no {prefix}.* decompilation leaves")

    [<Fact>]
    member _.``INSDC attributes become paired annotations, not tag-keyed properties`` () =
        // Arbitrary tag/value metadata belongs in the annotation layer, not the last-resort Properties
        // dump: the tag is the annotation term's Name and the value is its literal.
        let node = objectById analysis.Accession
        let ena = node.Annotations.Values |> Seq.find (fun annotation -> ArcTest.annotationName ir annotation = "ENA-STATUS")
        match ena.Value with
        | AnnotationValue.Literal(ArcValue.String v) -> Assert.Equal("PUBLIC", v)
        | v -> Assert.True(false, $"expected a string literal, got {v}")
        // Not duplicated into Properties, and the redundant flat structural leaves are suppressed.
        Assert.False(ArcTest.hasProperty "ENA-STATUS" node)
        Assert.False(
            node.Annotations.Values
            |> Seq.exists (fun annotation -> (ArcTest.annotationName ir annotation).EndsWith ".Attribute.Tag"))

    [<Fact>]
    member _.``all adapter terms resolve and all normalized identities match their map keys`` () =
        let nonEndpointIssues =
            Validation.validate ir
            |> List.filter (function
                | MissingEndpoint _ -> false
                | _ -> true)

        Assert.Empty nonEndpointIssues
        Assert.NotEmpty ir.Terms

    [<Fact>]
    member _.``the eight-entity fixture build produces deterministic graph identities`` () =
        let rebuilt =
            [ INSDC.bioProject project
              INSDC.study study
              INSDC.bioSample sample
              INSDC.experiment experiment
              INSDC.run run
              INSDC.analysis analysis
              INSDC.submission submission
              INSDC.receipt receipt ]
            |> INSDC.build

        Assert.Equal(ir, rebuilt)


type ArcResolverTests() =

    // The resolve-relations-afterwards pass prefers an accession, then a refcenter-namespaced refname,
    // then a bare refname, then a synthetic id from the refname.
    let target =
        GraphBuilder.object'
            "ACC1"
            ArcObjectKind.Collection
            []
            [ Vocabulary.Property.ofName "Alias", ArcValue.String "myAlias"
              Vocabulary.Property.ofName "CenterName", ArcValue.String "CENTER" ]
            []

    let pending accession refname refcenter =
        { Subject = ArcTest.id "S"
          Predicate = Vocabulary.Rel.hasStudy
          TargetAccession = accession
          TargetRefname = refname
          TargetRefcenter = refcenter }

    [<Fact>]
    member _.``an accession resolves directly, even when the target record is not loaded`` () =
        let edges = Mapping.resolveRelations [] [ pending (Some "ACCX") None None ]
        Assert.Equal(ArcTest.id "ACCX", (List.exactlyOne edges).Object)

    [<Fact>]
    member _.``a refname resolves to a loaded object within its refcenter namespace`` () =
        let edges = Mapping.resolveRelations [ target ] [ pending None (Some "myAlias") (Some "CENTER") ]
        Assert.Equal(ArcTest.id "ACC1", (List.exactlyOne edges).Object)

    [<Fact>]
    member _.``an unresolved refname falls back to a synthetic id`` () =
        let edges = Mapping.resolveRelations [] [ pending None (Some "ghost") None ]
        Assert.Equal(ArcTest.id "ghost", (List.exactlyOne edges).Object)


type IdentifierAnnotationTests() =

    // Folding INSDC <IDENTIFIERS> composites into single annotations (+ an edge when the namespace names
    // a modelled entity), preserving object integrity instead of shredding namespace/value/label.
    let externalId (ns: string) (value: string) =
        let q = QualifiedName(Namespace = ns, Value = value)
        let ids = Identifier()
        ids.ExternalId.Add q
        ids

    [<Fact>]
    member _.``an external identifier folds to one namespaced annotation`` () =
        let anns, _ = Annotations.identifierAnnotations "SUBJ" (externalId "Study" "DRP999")
        let ann = anns |> List.find (fun annotation -> ArcTest.localName annotation.Property = "Study")
        match ann.Value with
        | AnnotationValue.Literal(ArcValue.String v) -> Assert.Equal("DRP999", v)
        | v -> Assert.True(false, $"expected a string literal, got {v}")

    [<Fact>]
    member _.``an identifier naming a modelled entity draws a references edge`` () =
        let _, edges = Annotations.identifierAnnotations "SUBJ" (externalId "Study" "DRP999")
        let edge = List.exactlyOne edges
        Assert.Equal(ArcTest.id "SUBJ", edge.Subject)
        Assert.Equal(Vocabulary.Rel.references, edge.Predicate)
        Assert.Equal(Some "DRP999", edge.TargetAccession)

    [<Fact>]
    member _.``a self-referencing identifier folds but draws no edge`` () =
        let anns, edges = Annotations.identifierAnnotations "SUBJ" (externalId "BioProject" "SUBJ")
        Assert.NotEmpty anns
        Assert.Empty edges

    [<Fact>]
    member _.``an identifier in a non-modelled namespace draws no edge`` () =
        let _, edges = Annotations.identifierAnnotations "SUBJ" (externalId "PubMed" "12345")
        Assert.Empty edges


type IngestVocabularyTests() =

    // Lock the ingest vocabulary IRIs (house rule: vocabulary/term changes ship with a regression test).
    [<Fact>]
    member _.``ingest type and predicate terms use stable controlled-vocabulary IRIs`` () =
        Assert.Equal("http://purl.org/arc/insdc#Publication", Vocabulary.DType.publication.Value)
        Assert.Equal("http://purl.org/arc/insdc#CountMatrix", Vocabulary.DType.countMatrix.Value)
        Assert.Equal("http://purl.org/arc/insdc#CountColumn", Vocabulary.DType.countColumn.Value)
        Assert.Equal("http://purl.org/arc/insdc#hasColumn", Vocabulary.Rel.hasColumn.Value)

    [<Fact>]
    member _.``changing a property value does not rename its assertion slot`` () =
        let predicate = Vocabulary.Property.ofName "Title"
        let first = GraphBuilder.object' "TEST" ArcObjectKind.Collection [] [ predicate, ArcValue.String "before" ] []
        let second = GraphBuilder.object' "TEST" ArcObjectKind.Collection [] [ predicate, ArcValue.String "after" ] []

        Assert.Equal<Iri list>(first.Properties.Keys |> Seq.toList, second.Properties.Keys |> Seq.toList)
        Assert.NotEqual(first.Properties.Values |> Seq.exactlyOne, second.Properties.Values |> Seq.exactlyOne)


type IngestPaperTests() =

    // Ingesting a paper: the file becomes a `publication` Resource, its authors become deduped person
    // Agents, and it links to the dataset it describes via `references` edges that resolve once the INSDC
    // record is present. See plans/arcir-ingest.md.
    let read reader file = reader (TestFiles.fixture file) |> Seq.exactlyOne
    let project = read BioProject.read "PRJDB5192.xml"
    let projectIr = [ INSDC.bioProject project ] |> INSDC.build

    let jats = TestFiles.fixture "paper-PRJDB5192.jats.xml"
    let meta = IngestReaders.readJats jats
    let ir = Ingest.incorporate projectIr [ Ingest.paperFromJats jats [ project.Accession ] ]

    let paperId = ArcTest.id "doi:10.1000/testgenomics.2017.001"
    let outgoing predicate = ArcIR.outgoing paperId ir |> Seq.filter (fun r -> r.Predicate = predicate) |> List.ofSeq

    [<Fact>]
    member _.``readJats extracts title, doi and both authors`` () =
        Assert.Equal(Some "Epigenetic regulation in Arabidopsis thaliana Col-0", meta.Title)
        Assert.Equal(Some "10.1000/testgenomics.2017.001", meta.Doi)
        Assert.Equal(2, meta.Authors.Length)
        let names = meta.Authors |> List.choose (fun a -> a.Name)
        Assert.Contains("Tetsuji Kakutani", names)
        Assert.Contains("Jane Doe", names)

    [<Fact>]
    member _.``the paper is a publication Resource keyed by its doi, annotations-first`` () =
        let node = ir.Objects.[paperId]
        Assert.Equal(ArcObjectKind.Resource, node.Kind)
        Assert.True(ArcTest.hasType Vocabulary.DType.publication node)
        // paper-level metadata lands in annotations (house style); file metadata in Properties.
        Assert.True(node.Annotations.Values |> Seq.exists (fun annotation -> ArcTest.annotationName ir annotation = "Title"))
        Assert.True(ArcTest.hasProperty "Filename" node)

    [<Fact>]
    member _.``each author is a person Agent linked by hasContact`` () =
        let contacts = outgoing Vocabulary.Rel.hasContact
        Assert.Equal(2, contacts.Length)
        for edge in contacts do
            Assert.True(ArcTest.hasType Vocabulary.DType.person ir.Objects.[edge.Object])

    [<Fact>]
    member _.``the paper references the dataset, resolving onto the real project node`` () =
        let edge = outgoing Vocabulary.Rel.references |> List.exactlyOne
        Assert.Equal(ArcTest.id project.Accession, edge.Object)
        Assert.True(ir.Objects.ContainsKey edge.Object) // resolved, not dangling


type IngestAuthorMergeTests() =

    // An author whose email matches an existing contact's Agent id collapses to one enriched node
    // (merge-on-id): the paper enriches, rather than duplicates, a person already in the graph.
    let existingContact =
        GraphBuilder.object'
            "agent:jane@example.org"
            ArcObjectKind.Agent
            [ Vocabulary.DType.agent; Vocabulary.DType.person ]
            [ Vocabulary.Property.ofName "Organisation", ArcValue.String "Department of Integrative Genetics" ]
            []

    let paperResult =
        Ingest.paper
            { Title = Some "T"
              Doi = Some "10.1/x"
              Journal = None
              Authors = [ { Name = Some "Jane Doe"; Email = Some "jane@example.org"; Affiliation = None; Orcid = None } ] }
            { Name = "paper.pdf"; ByteSize = None; Checksum = None; MediaType = None }
            []

    let ir = Ingest.incorporate (GraphBuilder.assemble [ existingContact ] []) [ paperResult ]

    [<Fact>]
    member _.``a shared email collapses author and contact to one Agent with merged properties`` () =
        let node = ir.Objects.[ArcTest.id "agent:jane@example.org"]
        Assert.True(ArcTest.hasProperty "Organisation" node) // from the pre-existing contact
        Assert.True(ArcTest.hasProperty "Name" node) // from the paper author
        let personNodes = ir.Objects.Values |> Seq.filter (ArcTest.hasType Vocabulary.DType.person) |> Seq.length
        Assert.Equal(1, personNodes)


type IngestCountDataTests() =

    // Ingesting count data: the file is a `countMatrix` Resource; each run-accession column becomes a
    // `countColumn` fragment addressed by the RFC 7111 CSV selector `#col=<n>`, with a `producesData` edge
    // from its run (dangling until the run is merged). See plans/arcir-ingest.md.
    let tsv = IngestReaders.readCountFile (TestFiles.fixture "counts-PRJDB5192.tsv")
    let ir = [ Ingest.countData tsv ] |> INSDC.build
    let fileId = "count:counts-PRJDB5192.tsv"

    [<Fact>]
    member _.``the header parses to run-accession columns with 1-based positions`` () =
        Assert.Equal<CountColumn list>(
            [ { Index = 2; RunAccession = "DRR072834" }; { Index = 3; RunAccession = "DRR072835" } ],
            tsv.Columns)

    [<Fact>]
    member _.``the file is a countMatrix Resource with a countColumn fragment per run using RFC 7111 selectors`` () =
        Assert.True(ArcTest.hasType Vocabulary.DType.countMatrix ir.Objects.[ArcTest.id fileId])
        let col2 = ir.Objects.[ArcTest.id (fileId + "#col=2")]
        Assert.True(ArcTest.hasType Vocabulary.DType.countColumn col2)
        Assert.Equal(ArcValue.String "#col=2", ArcTest.propertyValue "FragmentSelector" col2)
        Assert.Equal(ArcValue.String "DRR072834", ArcTest.propertyValue "RunAccession" col2)
        Assert.True(ir.Objects.ContainsKey(ArcTest.id (fileId + "#col=3")))
        Assert.True(
            ArcIR.outgoing (ArcTest.id fileId) ir
            |> Seq.exists (fun r -> r.Predicate = Vocabulary.Rel.hasColumn && r.Object = ArcTest.id (fileId + "#col=2")))

    [<Fact>]
    member _.``each column receives a producesData edge from its run`` () =
        Assert.True(
            ArcIR.outgoing (ArcTest.id "DRR072834") ir
            |> Seq.exists (fun r -> r.Predicate = Vocabulary.Rel.producesData && r.Object = ArcTest.id (fileId + "#col=2")))

    [<Fact>]
    member _.``the zip reader yields the same count file as the loose tsv`` () =
        let fromZip = IngestReaders.readCountArchive (TestFiles.fixture "counts-PRJDB5192.zip") |> List.exactlyOne
        Assert.Equal<CountFile>(tsv, fromZip)


type GraphMlExportTests() =

    // The GraphML serializer renders the assembled ArcIR property graph so it can be opened in Gephi
    // (nodes colored by kind, properties + annotations as inspectable columns, predicates as edge labels).
    let read reader file = reader (TestFiles.fixture file) |> Seq.exactlyOne
    let sample = read BioSample.read "SAMD00064197.xml"
    let experiment = read Experiment.read "DRX066772.xml"

    let ir =
        [ INSDC.bioProject (read BioProject.read "PRJDB5192.xml")
          INSDC.study (read Study.read "DRP003416.xml")
          INSDC.bioSample sample
          INSDC.experiment experiment
          INSDC.run (read Run.read "DRR072834.xml")
          INSDC.analysis (read Analysis.read "ERZ496533.xml")
          INSDC.submission (read Submission.read "DRA005154.xml")
          INSDC.receipt (Receipt.read (TestFiles.fixture "receipt-sample.xml")) ]
        |> INSDC.build

    let gml = XNamespace.Get "http://graphml.graphdrawing.org/xmlns"
    let doc = XDocument.Parse(GraphMl.toString ir)
    let attr name (el: XElement) = el.Attribute(XName.Get name).Value
    let nodesOf (d: XDocument) = d.Root.Descendants(gml + "node")
    let edgesOf (d: XDocument) = d.Root.Descendants(gml + "edge")
    let nodeById id = nodesOf doc |> Seq.find (fun n -> attr "id" n = id)
    let nodeByRawId id = nodeById (ArcTest.id id).Value
    let dataVal keyId (el: XElement) =
        el.Elements(gml + "data")
        |> Seq.tryFind (fun d -> attr "key" d = keyId)
        |> Option.map (fun d -> d.Value)
    let keyIdOf forWhat name =
        doc.Root.Elements(gml + "key")
        |> Seq.find (fun k -> attr "for" k = forWhat && attr "attr.name" k = name)
        |> attr "id"

    [<Fact>]
    member _.``the export is a well-formed graphml document`` () =
        Assert.Equal("graphml", doc.Root.Name.LocalName)
        Assert.Equal("directed", attr "edgedefault" (doc.Root.Element(gml + "graph")))

    [<Fact>]
    member _.``the node label prefers the accession over the title`` () =
        // The experiment has a Title, but its label is the accession (the stable identity).
        Assert.Equal(Some((ArcTest.id experiment.Accession).Value), dataVal "label" (nodeByRawId experiment.Accession))

    [<Fact>]
    member _.``node and edge counts match the graph (dangling endpoints become placeholder nodes)`` () =
        let missing =
            ir.Relations.Values
            |> Seq.collect (fun r -> [ r.Subject; r.Object ])
            |> Seq.distinct
            |> Seq.filter (fun id -> not (ir.Objects.ContainsKey id))
            |> Seq.length
        Assert.Equal(ir.Objects.Count + missing, nodesOf doc |> Seq.length)
        Assert.Equal(ir.Relations.Count, edgesOf doc |> Seq.length)

    [<Fact>]
    member _.``a node carries its kind and a typed property renders in its own column`` () =
        Assert.Equal(Some "Activity", dataVal "kind" (nodeByRawId experiment.Accession))
        // The deduped taxon node's typed-integer TaxonId lands in the `TaxonId` column.
        Assert.Equal(Some "3702", dataVal (keyIdOf "node" "TaxonId") (nodeByRawId "taxon:3702"))

    [<Fact>]
    member _.``annotations are rendered as their own columns, not counted`` () =
        // The BioSample's `ecotype` attribute is an annotation; it must serialize as a populated node
        // column (keyed by the annotation term's name) rather than a bare count.
        let value = dataVal (keyIdOf "node" "ecotype") (nodeByRawId sample.Accession)
        Assert.Equal(Some "Col-0", value)

    [<Fact>]
    member _.``an edge carries its predicate as the label`` () =
        let studyEdge =
            edgesOf doc
            |> Seq.find (fun e ->
                attr "source" e = (ArcTest.id experiment.Accession).Value
                && attr "target" e = (ArcTest.id "DRP003416").Value)
        Assert.Equal(Some "hasStudy", dataVal "predicate" studyEdge)

    [<Fact>]
    member _.``a relation to a missing target yields a placeholder node and a valid edge`` () =
        let node = GraphBuilder.object' "A" ArcObjectKind.Collection [] [] []
        let relation = GraphBuilder.relation "A" Vocabulary.Rel.hasStudy "B" [] []
        let dangling = GraphBuilder.assemble [ node ] [ relation ]
        let d = XDocument.Parse(GraphMl.toString dangling)
        let nodes = nodesOf d |> Seq.toList
        Assert.Equal(2, nodes.Length)
        let placeholder = nodes |> Seq.find (fun n -> attr "id" n = (ArcTest.id "B").Value)
        Assert.Equal(Some "Missing", dataVal "kind" placeholder)
        Assert.Equal(1, edgesOf d |> Seq.length)


type HtmlExportTests() =

    // The interactive HTML viewer renders the same graph as a single self-contained page with an
    // embedded force-directed SVG and a click-to-inspect property/annotation panel.
    let read reader file = reader (TestFiles.fixture file) |> Seq.exactlyOne

    let ir =
        [ INSDC.bioProject (read BioProject.read "PRJDB5192.xml")
          INSDC.study (read Study.read "DRP003416.xml")
          INSDC.bioSample (read BioSample.read "SAMD00064197.xml")
          INSDC.experiment (read Experiment.read "DRX066772.xml")
          INSDC.run (read Run.read "DRR072834.xml")
          INSDC.analysis (read Analysis.read "ERZ496533.xml")
          INSDC.submission (read Submission.read "DRA005154.xml")
          INSDC.receipt (Receipt.read (TestFiles.fixture "receipt-sample.xml")) ]
        |> INSDC.build

    let html = Html.toString ir

    [<Fact>]
    member _.``the export is a self-contained html document with no external resources`` () =
        Assert.StartsWith("<!doctype html", html)
        Assert.Contains("<svg", html)
        Assert.Contains("const DATA =", html)
        // Everything is inline: no CDN script/stylesheet, no network dependency.
        Assert.DoesNotContain("<script src", html)
        Assert.DoesNotContain("<link", html)

    [<Fact>]
    member _.``node properties and rendered annotations are embedded for inspection`` () =
        // The deduped taxon node and its typed TaxonId property + organism annotation are in the payload.
        Assert.Contains("taxon:3702", html)
        Assert.Contains("Arabidopsis thaliana", html)

    [<Fact>]
    member _.``dangling reference targets are embedded as Missing placeholder nodes`` () =
        Assert.Contains("\"Missing\"", html)

    [<Fact>]
    member _.``a value containing markup cannot break out of the script block`` () =
        let evil = GraphBuilder.object' "x" ArcObjectKind.Resource [] [ Vocabulary.Property.ofName "Note", ArcValue.String "</script><b>hi" ] []
        let out = Html.toString (GraphBuilder.assemble [ evil ] [])
        Assert.DoesNotContain("</script><b>hi", out)
        Assert.Contains("\\u003c/script>", out)

type SampleReferenceTests() =

    [<Fact>]
    member _.``experiment resolves its sample reference to the BioSample node, not the SRA sample accession`` () =
        // DRX066772's SAMPLE_DESCRIPTOR references the SRA sample accession DRS039895, but the
        // BioSample node is keyed by its BioSample accession SAMD00064197; the descriptor's
        // EXTERNAL_ID[namespace=BioSample] carries that key, so the hasSample edge must resolve to
        // SAMD00064197 and never dangle to DRS039895.
        let experiment = Experiment.read (TestFiles.fixture "DRX066772.xml") |> Seq.exactlyOne
        let sample = BioSample.read (TestFiles.fixture "SAMD00064197.xml") |> Seq.exactlyOne
        let ir = INSDC.build [ INSDC.experiment experiment; INSDC.bioSample sample ]

        let sampleTargets =
            ir.Relations.Values
            |> Seq.filter (fun r -> r.Subject = ArcTest.id "DRX066772" && r.Predicate = Vocabulary.Rel.hasSample)
            |> Seq.map (fun r -> r.Object)
            |> Seq.toList

        Assert.Contains(ArcTest.id "SAMD00064197", sampleTargets)
        Assert.DoesNotContain(ArcTest.id "DRS039895", sampleTargets)
