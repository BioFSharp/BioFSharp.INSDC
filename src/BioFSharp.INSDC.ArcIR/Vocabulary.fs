namespace BioFSharp.INSDC.ArcIR

open Arc.Build

/// The controlled vocabulary for the INSDC -> ArcIR mapping: relation predicates (`Rel`) and object
/// DTypes (`DType`), minted as real IRIs under one documented base. This is the single place predicate
/// and type identity is defined — converters and the resolver reference these constants instead of
/// hand-typing strings, so the whole graph speaks one vocabulary.
///
/// Field-level property keys are deliberately not formalized here. The current proof-of-concept
/// converters write explicit annotations and keep `ArcObject.Properties` keys as short convenience
/// names; the Phase 2 target-neutral model will replace these shapes in one breaking change.
[<RequireQualifiedAccess>]
module Vocabulary =

    /// Base IRI all mapping-defined terms hang off. Placeholder — swap for a registered PURL/w3id base.
    [<Literal>]
    let BaseIri = "http://purl.org/arc/insdc#"

    let private term (localName: string) = Iri.Create(BaseIri + localName)

    /// Relation predicates, read subject --predicate--> object.
    [<RequireQualifiedAccess>]
    module Rel =

        // Cross-entity references (RefObject-based).
        let hasStudy = term "hasStudy" // Experiment/Analysis -> Study        (~ prov:used)
        let hasSample = term "hasSample" // Experiment/Analysis -> BioSample   (~ RO:0002233 has input)
        let hasExperiment = term "hasExperiment" // Run/Analysis -> Experiment
        let hasRun = term "hasRun" // Analysis -> Run
        let hasAnalysis = term "hasAnalysis" // Analysis -> Analysis (derivation)

        // Project-to-project links (RelatedProjects; not RefObject).
        let hasParentProject = term "hasParentProject"
        let hasChildProject = term "hasChildProject"
        let hasPeerProject = term "hasPeerProject"

        // Receipt acknowledgements (Id buckets; not RefObject).
        let acknowledges = term "acknowledges"

        // Cross-reference from an identifier (IDENTIFIERS/EXTERNAL_ID etc.) to the entity it names.
        let references = term "references"

        // Sub-object edges.
        let hasOrganism = term "hasOrganism" // BioSample -> Organism/Taxon
        let usesInstrument = term "usesInstrument" // Experiment/Run -> Instrument
        let hasProtocol = term "hasProtocol" // Experiment -> Recipe/Protocol
        let producesData = term "producesData" // Analysis/Run -> Resource/Data  (~ prov:wasGeneratedBy, inverse)
        let hasContact = term "hasContact" // entity -> Agent
        let hasRole = term "hasRole" // Agent -> Role

        // Ingested supplementary sources (see plans/arcir-ingest.md).
        let hasColumn = term "hasColumn" // count matrix file -> a per-run column fragment

    /// Object DTypes — the open, IRI-typed semantic tags carried on `ArcObject.DTypes`.
    [<RequireQualifiedAccess>]
    module DType =

        let bioProject = term "BioProject"
        let investigation = term "Investigation"
        let study = term "Study"
        let bioSample = term "BioSample"
        let sample = term "Sample"
        let organism = term "Organism"
        let taxon = term "Taxon"
        let experiment = term "Experiment"
        let assay = term "Assay"
        let run = term "Run"
        let analysis = term "Analysis"
        let submission = term "Submission"
        let receipt = term "Receipt"
        let instrument = term "Instrument"
        let protocol = term "Protocol"
        let data = term "Data"
        let agent = term "Agent"
        let person = term "Person"
        let organization = term "Organization"
        let role = term "Role"

        // Ingested supplementary sources (see plans/arcir-ingest.md).
        let publication = term "Publication" // a paper/article resource describing a dataset
        let countMatrix = term "CountMatrix" // a count/expression matrix file
        let countColumn = term "CountColumn" // one column of a count matrix (a per-run profile)
