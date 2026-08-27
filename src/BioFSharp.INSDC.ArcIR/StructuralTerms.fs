namespace BioFSharp.INSDC.ArcIR

open BioFSharp.ArcIR

/// Canonical INSDCER structural terms currently emitted directly by the F1 adapter.
[<RequireQualifiedAccess>]
module StructuralTerms =

    /// Repository-owned expansion used until a persistent INSDCER namespace is registered.
    [<Literal>]
    let BaseIri = "https://github.com/nfdi4plants/ER_ontologies/tree/main/ontologies/INSDC?term=INSDCER_"

    let private term localId = Iri.Create(BaseIri + localId)

    let private bioProjectSource =
        "https://github.com/nfdi4plants/ER_ontologies/blob/main/ontologies/INSDC/INSDC.BioProject.obo"

    let private studySource =
        "https://github.com/nfdi4plants/ER_ontologies/blob/main/ontologies/INSDC/INSDC.Study.obo"

    /// BioProject administrative field terms.
    [<RequireQualifiedAccess>]
    module BioProject =

        /// Archive-assigned BioProject accession.
        let archiveAccession = term "1000001"
        /// BioProject title.
        let title = term "1000014"
        /// BioProject description.
        let description = term "1000015"
        /// Date on which the BioProject first became public.
        let firstPublicDate = term "1000017"

    /// Study administrative field terms.
    [<RequireQualifiedAccess>]
    module Study =

        /// Archive-assigned Study accession.
        let archiveAccession = term "2000001"
        /// Study title.
        let title = term "2000014"
        /// Study description.
        let description = term "2000019"

    let private definitions =
        [ BioProject.archiveAccession, OntologyTerm.create (Some "BioProject archive accession") (Some bioProjectSource)
          BioProject.title, OntologyTerm.create (Some "BioProject title") (Some bioProjectSource)
          BioProject.description, OntologyTerm.create (Some "BioProject description") (Some bioProjectSource)
          BioProject.firstPublicDate, OntologyTerm.create (Some "BioProject first public date") (Some bioProjectSource)
          Study.archiveAccession, OntologyTerm.create (Some "Study archive accession") (Some studySource)
          Study.title, OntologyTerm.create (Some "Study title") (Some studySource)
          Study.description, OntologyTerm.create (Some "Study description") (Some studySource) ]
        |> Map.ofList

    /// Returns the checked-in source definition for a known emitted INSDCER term.
    let tryDefinition termId = Map.tryFind termId definitions
