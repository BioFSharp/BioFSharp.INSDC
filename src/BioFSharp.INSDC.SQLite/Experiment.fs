namespace BioFSharp.INSDC.SQLite

open System
open Microsoft.Data.Sqlite
open BioFSharp.FileFormats.INSDC
open BioFSharp.INSDC.SQLite.Internal

/// SQLite persistence for `Experiment` — the heaviest entity. Spans the core
/// row plus design (library descriptor + layout + spot descriptor + targeted
/// loci), platform (kind + params bag), the two outgoing references
/// (`StudyRef`, `Library.SampleDescriptor`), and the standard identifiers /
/// attributes / links collections.
module Experiment =

    [<Literal>]
    let private accessionColumn = "experiment_accession"

    let private identifierOwner accession : IdentifierOwner =
        { Table = "experiment_identifiers"; AccessionColumn = accessionColumn; Accession = accession }

    let private attributeOwner accession : AttributeOwner =
        { Table = "experiment_attributes"; AccessionColumn = accessionColumn; Accession = accession }

    let private linkOwner accession : LinkOwner =
        { Table = "experiment_links"; AccessionColumn = accessionColumn; Accession = accession }

    let private studyRefOwner accession : RefObjectOwner =
        { Table = "experiment_study_ref"
          IdentifiersTable = "experiment_study_ref_identifiers"
          AccessionColumn = accessionColumn
          Accession = accession }

    let private sampleDescriptorOwner accession : RefObjectOwner =
        { Table = "experiment_sample_descriptor"
          IdentifiersTable = "experiment_sample_descriptor_identifiers"
          AccessionColumn = accessionColumn
          Accession = accession }

    let private platformOwner accession : PlatformOwner =
        { Table = "experiment_platform"
          ParamsTable = "experiment_platform_params"
          AccessionColumn = accessionColumn
          Accession = accession }

    let private insertCore (connection: SqliteConnection) (exp: Experiment) (studyAccession: string) : unit =
        Sql.execNonQuery
            connection
            "INSERT INTO experiment (accession, alias, center_name, broker_name, title, study_accession, sample_demux_directive) \
             VALUES (@acc, @alias, @cn, @bn, @title, @study, @demux);"
            [
                "@acc", box exp.Accession
                "@alias", box exp.Alias
                "@cn", box exp.CenterName
                "@bn", box exp.BrokerName
                "@title", box exp.Title
                "@study", box studyAccession
                "@demux", null
            ]
        |> ignore

    let private insertDesign (connection: SqliteConnection) (exp: Experiment) : unit =
        match exp.Design with
        | null -> ()
        | design ->
            let descriptor = design.LibraryDescriptor
            let descriptorParts =
                if isNull descriptor then
                    null, null, null, null, null, Nullable<int64>(), Nullable<double>(), null, null
                else
                    let layoutKind, nominalLength, nominalSdev =
                        match descriptor.LibraryLayout with
                        | null -> null, Nullable<int64>(), Nullable<double>()
                        | layout when not (isNull layout.Paired) ->
                            "PAIRED", layout.Paired.NominalLength, layout.Paired.NominalSdev
                        | layout when not (isNull layout.Single) ->
                            "SINGLE", Nullable<int64>(), Nullable<double>()
                        | _ -> null, Nullable<int64>(), Nullable<double>()
                    descriptor.LibraryName,
                    string descriptor.LibraryStrategy,
                    string descriptor.LibrarySource,
                    string descriptor.LibrarySelection,
                    layoutKind,
                    nominalLength,
                    nominalSdev,
                    descriptor.PoolingStrategy,
                    descriptor.LibraryConstructionProtocol
            let libName, libStrategy, libSource, libSelection, layoutKind, nominalLength, nominalSdev, poolingStrategy, libConstructionProtocol =
                descriptorParts
            Sql.execNonQuery
                connection
                "INSERT INTO experiment_design \
                    (experiment_accession, design_description, library_name, library_construction_protocol, \
                     library_strategy, library_source, library_selection, \
                     library_layout_kind, library_layout_nominal_length, library_layout_nominal_sdev, pooling_strategy) \
                 VALUES (@acc, @descDesc, @libName, @libCP, @libStrat, @libSrc, @libSel, @layoutKind, @nomLen, @nomSdev, @pool);"
                [
                    "@acc", box exp.Accession
                    "@descDesc", box design.DesignDescription
                    "@libName", box libName
                    "@libCP", box libConstructionProtocol
                    "@libStrat", box libStrategy
                    "@libSrc", box libSource
                    "@libSel", box libSelection
                    "@layoutKind", box layoutKind
                    "@nomLen", (if nominalLength.HasValue then box nominalLength.Value else null)
                    "@nomSdev", (if nominalSdev.HasValue then box nominalSdev.Value else null)
                    "@pool", box poolingStrategy
                ]
            |> ignore

            // Targeted loci — one row per locus; locus_name is part of the PK.
            if not (isNull descriptor) && not (isNull descriptor.TargetedLoci) then
                for locus in descriptor.TargetedLoci do
                    let locusName =
                        if locus.LocusName.HasValue then string locus.LocusName.Value
                        else
                            failwithf
                                "Experiment %s has a TargetedLoci entry without a locus_name; cannot persist."
                                exp.Accession
                    let probeDb, probeId, probeLabel =
                        match locus.ProbeSet with
                        | null -> null, null, null
                        | xref -> xref.Db, xref.Id, xref.Label
                    Sql.execNonQuery
                        connection
                        "INSERT INTO experiment_targeted_loci \
                            (experiment_accession, locus_name, probe_set_db, probe_set_id, probe_set_label, description) \
                         VALUES (@acc, @locus, @db, @id, @label, @desc);"
                        [
                            "@acc", box exp.Accession
                            "@locus", box locusName
                            "@db", box probeDb
                            "@id", box probeId
                            "@label", box probeLabel
                            "@desc", box locus.Description
                        ]
                    |> ignore

            // Spot descriptor — flatten the SpotDecodeSpec.ReadSpec collection;
            // SpotLength is repeated on every row to match the schema's per-row shape.
            match design.SpotDescriptor with
            | null -> ()
            | sd ->
                match sd.SpotDecodeSpec with
                | null -> ()
                | spec ->
                    let spotLength =
                        if spec.SpotLength.HasValue then box spec.SpotLength.Value else null
                    for rs in spec.ReadSpec do
                        Sql.execNonQuery
                            connection
                            "INSERT INTO experiment_spot_descriptor \
                                (experiment_accession, read_index, spot_length, item, read_class, read_label, read_type, base_coord) \
                             VALUES (@acc, @idx, @spotLen, @item, @class, @label, @type, @baseCoord);"
                            [
                                "@acc", box exp.Accession
                                "@idx", box rs.ReadIndex
                                "@spotLen", spotLength
                                "@item", box "READ_SPEC"
                                "@class", box (string rs.ReadClass)
                                "@label", box rs.ReadLabel
                                "@type", box (string rs.ReadType)
                                "@baseCoord", (if rs.BaseCoord.HasValue then box rs.BaseCoord.Value else null)
                            ]
                        |> ignore

    /// Persists `experiment` and every row it deconstructs into. `studyAccession`
    /// is the parent Study's accession (the schema's experiment.study_accession
    /// is NOT NULL — every experiment must belong to a study). The matching
    /// Study row must already exist or the INSERT will fail on the FK.
    let insert (connection: SqliteConnection) (studyAccession: string) (experiment: Experiment) : unit =
        Sql.withTransaction connection (fun _tx ->
            insertCore connection experiment studyAccession
            insertDesign connection experiment
            Platforms.write connection (platformOwner experiment.Accession) experiment.Platform
            Identifiers.write connection (identifierOwner experiment.Accession) experiment.Identifiers
            Attributes.write connection (attributeOwner experiment.Accession) experiment.ExperimentAttributes
            Links.write connection (linkOwner experiment.Accession) experiment.ExperimentLinks
            References.write connection (studyRefOwner experiment.Accession) experiment.StudyRef
            if not (isNull experiment.Design) then
                References.write connection (sampleDescriptorOwner experiment.Accession) experiment.Design.SampleDescriptor)

    let private parseEnum<'T when 'T : struct
                                and 'T :> Enum
                                and 'T : (new : unit -> 'T)>
        (text: string)
        : 'T =
        Enum.Parse(typeof<'T>, text) :?> 'T

    let private readDesign (connection: SqliteConnection) (accession: string) (sampleDescriptor: BioSampleDescriptor) : Library =
        let coreRow =
            Sql.tryQueryOne
                connection
                "SELECT design_description, library_name, library_construction_protocol, \
                        library_strategy, library_source, library_selection, \
                        library_layout_kind, library_layout_nominal_length, library_layout_nominal_sdev, pooling_strategy \
                 FROM experiment_design WHERE experiment_accession = @acc;"
                [ "@acc", box accession ]
                (fun reader ->
                    Sql.readStringOrNull reader 0,
                    Sql.readStringOrNull reader 1,
                    Sql.readStringOrNull reader 2,
                    Sql.readStringOrNull reader 3,
                    Sql.readStringOrNull reader 4,
                    Sql.readStringOrNull reader 5,
                    Sql.readStringOrNull reader 6,
                    (if reader.IsDBNull(7) then Nullable<int64>() else Nullable(reader.GetInt64(7))),
                    (if reader.IsDBNull(8) then Nullable<double>() else Nullable(reader.GetDouble(8))),
                    Sql.readStringOrNull reader 9)
        let lociRows =
            Sql.queryAll
                connection
                "SELECT locus_name, probe_set_db, probe_set_id, probe_set_label, description \
                 FROM experiment_targeted_loci WHERE experiment_accession = @acc ORDER BY locus_name;"
                [ "@acc", box accession ]
                (fun reader ->
                    reader.GetString(0),
                    Sql.readStringOrNull reader 1,
                    Sql.readStringOrNull reader 2,
                    Sql.readStringOrNull reader 3,
                    Sql.readStringOrNull reader 4)
        let spotRows =
            Sql.queryAll
                connection
                "SELECT read_index, spot_length, read_class, read_label, read_type, base_coord \
                 FROM experiment_spot_descriptor WHERE experiment_accession = @acc ORDER BY read_index;"
                [ "@acc", box accession ]
                (fun reader ->
                    reader.GetInt64(0),
                    (if reader.IsDBNull(1) then Nullable<int64>() else Nullable(reader.GetInt64(1))),
                    Sql.readStringOrNull reader 2,
                    Sql.readStringOrNull reader 3,
                    Sql.readStringOrNull reader 4,
                    (if reader.IsDBNull(5) then Nullable<int64>() else Nullable(reader.GetInt64(5))))

        let hasAnything =
            coreRow.IsSome
            || not (List.isEmpty lociRows)
            || not (List.isEmpty spotRows)
            || not (isNull sampleDescriptor)
        if not hasAnything then null else

        let library = Library()
        library.SampleDescriptor <- sampleDescriptor
        match coreRow with
        | Some (designDesc, libName, libCP, libStrat, libSrc, libSel, layoutKind, nomLen, nomSdev, pooling) ->
            library.DesignDescription <- designDesc
            let descriptor = LibraryDescriptor()
            descriptor.LibraryName <- libName
            descriptor.LibraryConstructionProtocol <- libCP
            descriptor.PoolingStrategy <- pooling
            if not (isNull libStrat) then
                descriptor.LibraryStrategy <- parseEnum<LibraryStrategy> libStrat
            if not (isNull libSrc) then
                descriptor.LibrarySource <- parseEnum<LibrarySource> libSrc
            if not (isNull libSel) then
                descriptor.LibrarySelection <- parseEnum<LibrarySelection> libSel
            match layoutKind with
            | "SINGLE" ->
                let layout = LibraryDescriptorLibraryLayout()
                layout.Single <- LibraryDescriptorLibraryLayoutSingle()
                descriptor.LibraryLayout <- layout
            | "PAIRED" ->
                let layout = LibraryDescriptorLibraryLayout()
                let paired = LibraryDescriptorLibraryLayoutPaired()
                paired.NominalLength <- nomLen
                paired.NominalSdev <- nomSdev
                layout.Paired <- paired
                descriptor.LibraryLayout <- layout
            | _ -> ()
            for (locusName, db, id, label, desc) in lociRows do
                let locus = LibraryDescriptorTargetedLociLocus()
                locus.LocusName <-
                    Nullable<LibraryDescriptorTargetedLociLocusLocusName>(
                        parseEnum<LibraryDescriptorTargetedLociLocusLocusName> locusName)
                if not (isNull db) || not (isNull id) || not (isNull label) then
                    locus.ProbeSet <- XRef(Db = db, Id = id, Label = label)
                locus.Description <- desc
                descriptor.TargetedLoci.Add(locus)
            library.LibraryDescriptor <- descriptor
        | None -> ()

        if not (List.isEmpty spotRows) then
            let spotDescriptor = SpotDescriptor()
            let spec = SpotDescriptorSpotDecodeSpec()
            let firstSpotLength = spotRows |> List.tryPick (fun (_, len, _, _, _, _) -> if len.HasValue then Some len.Value else None)
            match firstSpotLength with
            | Some len -> spec.SpotLength <- Nullable(len)
            | None -> ()
            for (idx, _, cls, label, rtype, baseCoord) in spotRows do
                let rs = SpotDescriptorSpotDecodeSpecReadSpec()
                rs.ReadIndex <- idx
                rs.ReadLabel <- label
                if not (isNull cls) then
                    rs.ReadClass <- parseEnum<SpotDescriptorSpotDecodeSpecReadSpecReadClass> cls
                if not (isNull rtype) then
                    rs.ReadType <- parseEnum<SpotDescriptorSpotDecodeSpecReadSpecReadType> rtype
                rs.BaseCoord <- baseCoord
                spec.ReadSpec.Add(rs)
            spotDescriptor.SpotDecodeSpec <- spec
            library.SpotDescriptor <- spotDescriptor

        library

    /// Reconstructs an `Experiment` from its accession by joining every owner
    /// table. Returns `None` when no core row exists.
    let tryGet (connection: SqliteConnection) (accession: string) : Experiment option =
        let core =
            Sql.tryQueryOne
                connection
                "SELECT alias, center_name, broker_name, title FROM experiment WHERE accession = @acc;"
                [ "@acc", box accession ]
                (fun reader ->
                    Sql.readStringOrNull reader 0,
                    Sql.readStringOrNull reader 1,
                    Sql.readStringOrNull reader 2,
                    Sql.readStringOrNull reader 3)
        match core with
        | None -> None
        | Some (alias, centerName, brokerName, title) ->
            let experiment = Experiment()
            experiment.Accession <- accession
            experiment.Alias <- alias
            experiment.CenterName <- centerName
            experiment.BrokerName <- brokerName
            experiment.Title <- title
            experiment.StudyRef <- References.read<ExperimentStudyRef> connection (studyRefOwner accession)
            let sampleDescriptor = References.read<BioSampleDescriptor> connection (sampleDescriptorOwner accession)
            experiment.Design <- readDesign connection accession sampleDescriptor
            experiment.Platform <- Platforms.read connection (platformOwner accession)
            experiment.Identifiers <- Identifiers.read connection (identifierOwner accession)
            for attr in Attributes.read connection (attributeOwner accession) do
                experiment.ExperimentAttributes.Add(attr)
            for link in Links.read connection (linkOwner accession) do
                experiment.ExperimentLinks.Add(link)
            Some experiment

    /// Removes the row from `experiment`; cascades through every owned table.
    let delete (connection: SqliteConnection) (accession: string) : unit =
        Sql.withTransaction connection (fun _ ->
            Sql.execNonQuery
                connection
                "DELETE FROM experiment WHERE accession = @acc;"
                [ "@acc", box accession ]
            |> ignore)

    /// Lists every Experiment accession in the database, lexicographically.
    let listAccessions (connection: SqliteConnection) : string seq =
        Sql.queryAll
            connection
            "SELECT accession FROM experiment ORDER BY accession;"
            []
            (fun reader -> reader.GetString(0))
        :> string seq
