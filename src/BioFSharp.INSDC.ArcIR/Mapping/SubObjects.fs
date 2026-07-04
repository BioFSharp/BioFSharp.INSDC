namespace BioFSharp.INSDC.ArcIR

open System
open Arc.Build
open BioFSharp.FileFormats.INSDC

/// Reusable builders that turn nested INSDC structures into first-class ArcIR sub-object nodes plus the
/// edge linking each to its parent. Every builder returns `(node, parentEdge)`. Ids are deterministic so
/// shared things — a taxon, an instrument model, a person/institution — collapse to a single node via
/// `ArcIR.addObject`'s merge-on-id when several parents reference them.
[<RequireQualifiedAccess>]
module SubObjects =

    let private iri (s: string) = Iri.Create s
    let private orEmpty (s: string) = if isNull s then "" else s
    let private choose xs = xs |> List.choose id
    let private strProp = ArcValueConversion.stringProp

    /// The organism/taxon behind a BioSample. Id is the NCBI taxon id, so one Taxon node is shared across
    /// every sample of the same organism.
    let organism (parentId: string) (name: BioSampleName) : ArcObject * ArcRelation =
        let id = "taxon:" + string name.TaxonId
        let props =
            choose
                [ Some(iri "TaxonId", ArcValueConversion.ofInt name.TaxonId)
                  strProp "ScientificName" name.ScientificName
                  strProp "CommonName" name.CommonName
                  strProp "DisplayName" name.DisplayName ]
        let node = ArcObject.create id ArcObjectKind.Observable [ Vocabulary.DType.organism; Vocabulary.DType.taxon ] props []
        node, ArcRelation.create parentId Vocabulary.Rel.hasOrganism id [] []

    let private agentNode id props =
        ArcObject.create id ArcObjectKind.Agent [ Vocabulary.DType.agent; Vocabulary.DType.person ] props []

    /// A DAC contact (name/email/phone/org) as an Agent. Deduped by email, else by name+organisation.
    let dacContact (parentId: string) (c: DacContactsContact) : (ArcObject * ArcRelation) option =
        let key = if not (String.IsNullOrWhiteSpace c.Email) then orEmpty c.Email else orEmpty c.Name + "|" + orEmpty c.Organisation
        if String.IsNullOrWhiteSpace key then
            None
        else
            let id = "agent:" + key.Trim().ToLowerInvariant()
            let props =
                choose
                    [ strProp "Name" c.Name
                      strProp "Email" c.Email
                      strProp "TelephoneNumber" c.TelephoneNumber
                      strProp "Organisation" c.Organisation ]
            Some(agentNode id props, ArcRelation.create parentId Vocabulary.Rel.hasContact id [] [])

    /// A submission contact (name + notify addresses) as an Agent, deduped by name.
    let submissionContact (parentId: string) (c: SubmissionContactsContact) : (ArcObject * ArcRelation) option =
        if String.IsNullOrWhiteSpace c.Name then
            None
        else
            let id = "agent:" + c.Name.Trim().ToLowerInvariant()
            let props =
                choose
                    [ strProp "Name" c.Name
                      strProp "InformOnStatus" c.InformOnStatus
                      strProp "InformOnError" c.InformOnError ]
            Some(agentNode id props, ArcRelation.create parentId Vocabulary.Rel.hasContact id [] [])

    /// An institution (center/broker/lab name) as an Agent(organization), deduped by name.
    let organization (parentId: string) (institution: string) : (ArcObject * ArcRelation) option =
        if String.IsNullOrWhiteSpace institution then
            None
        else
            let id = "org:" + institution.Trim().ToLowerInvariant()
            let node = ArcObject.create id ArcObjectKind.Agent [ Vocabulary.DType.agent; Vocabulary.DType.organization ] [ iri "Name", ArcValue.String institution ] []
            Some(node, ArcRelation.create parentId Vocabulary.Rel.hasContact id [] [])

    /// The instrument behind an Experiment/Run `Platform`. The `Platform` is a choice of ~18 technologies,
    /// each exposing an `InstrumentModel` enum; reflect to find the chosen one. Deduped by model.
    let instrument (parentId: string) (platform: Platform) : (ArcObject * ArcRelation) option =
        let chosen =
            platform.GetType().GetProperties()
            |> Array.tryPick (fun p ->
                let v = p.GetValue platform
                if not (isNull v) && p.PropertyType.Name.StartsWith "Platform" then Some(p.Name, v) else None)
        match chosen with
        | None -> None
        | Some(tech, techObj) ->
            let modelValue =
                match techObj.GetType().GetProperty "InstrumentModel" with
                | null -> None
                | mp -> ArcValueConversion.ofEnumObj (mp.GetValue techObj)
            let id =
                match modelValue with
                | Some(ArcValue.Iri model) -> "instrument:" + model.Value
                | _ -> "instrument:" + tech
            let props =
                choose
                    [ Some(iri "Platform", ArcValue.String tech)
                      modelValue |> Option.map (fun v -> iri "InstrumentModel", v) ]
            let node = ArcObject.create id ArcObjectKind.Instrument [ Vocabulary.DType.instrument ] props []
            Some(node, ArcRelation.create parentId Vocabulary.Rel.usesInstrument id [] [])

    /// The library-prep protocol behind an Experiment's `Design`. Experiment-scoped (not deduped).
    let protocol (parentId: string) (lib: LibraryDescriptor) : ArcObject * ArcRelation =
        let id = parentId + "#library"
        let props =
            choose
                [ strProp "LibraryName" lib.LibraryName
                  Some(iri "LibraryStrategy", ArcValueConversion.ofEnum lib.LibraryStrategy)
                  Some(iri "LibrarySource", ArcValueConversion.ofEnum lib.LibrarySource)
                  Some(iri "LibrarySelection", ArcValueConversion.ofEnum lib.LibrarySelection)
                  strProp "PoolingStrategy" lib.PoolingStrategy
                  strProp "LibraryConstructionProtocol" lib.LibraryConstructionProtocol ]
        let node = ArcObject.create id ArcObjectKind.Recipe [ Vocabulary.DType.protocol ] props []
        node, ArcRelation.create parentId Vocabulary.Rel.hasProtocol id [] []

    /// An Analysis data file as a Resource. Scoped to its producer by filename.
    let analysisFile (parentId: string) (f: AnalysisFile) : ArcObject * ArcRelation =
        let id = parentId + "#file:" + orEmpty f.Filename
        let props =
            choose
                [ strProp "Filename" f.Filename
                  Some(iri "Filetype", ArcValueConversion.ofEnum f.Filetype)
                  strProp "Checksum" f.Checksum
                  Some(iri "ChecksumMethod", ArcValueConversion.ofEnum f.ChecksumMethod) ]
        let node = ArcObject.create id ArcObjectKind.Resource [ Vocabulary.DType.data ] props []
        node, ArcRelation.create parentId Vocabulary.Rel.producesData id [] []

    /// A Run data-block file as a Resource. Scoped to its producer by filename.
    let runFile (parentId: string) (f: RunDataBlockFilesFile) : ArcObject * ArcRelation =
        let id = parentId + "#file:" + orEmpty f.Filename
        let props =
            choose
                [ strProp "Filename" f.Filename
                  Some(iri "Filetype", ArcValueConversion.ofEnum f.Filetype)
                  strProp "Checksum" f.Checksum ]
        let node = ArcObject.create id ArcObjectKind.Resource [ Vocabulary.DType.data ] props []
        node, ArcRelation.create parentId Vocabulary.Rel.producesData id [] []
