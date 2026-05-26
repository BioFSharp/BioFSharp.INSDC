namespace BioFSharp.INSDC.SQLite

open System
open Microsoft.Data.Sqlite
open BioFSharp.FileFormats.INSDC
open BioFSharp.INSDC.SQLite.Internal

/// SQLite persistence for `Run`. Mirrors `Experiment` (core row, optional
/// spot descriptor, optional platform, identifiers/attributes/links) but with
/// a single outgoing reference (`ExperimentRef`) plus a slim `RunDataBlock`
/// for the file payload. Run.Processing / Run.RunProperty / DataBlock.Files
/// are deferred (plan section D4).
module Run =

    [<Literal>]
    let private accessionColumn = "run_accession"

    let private identifierOwner accession : IdentifierOwner =
        { Table = "run_identifiers"; AccessionColumn = accessionColumn; Accession = accession }

    let private attributeOwner accession : AttributeOwner =
        { Table = "run_attributes"; AccessionColumn = accessionColumn; Accession = accession }

    let private linkOwner accession : LinkOwner =
        { Table = "run_links"; AccessionColumn = accessionColumn; Accession = accession }

    let private experimentRefOwner accession : RefObjectOwner =
        { Table = "run_experiment_ref"
          IdentifiersTable = "run_experiment_ref_identifiers"
          AccessionColumn = accessionColumn
          Accession = accession }

    let private platformOwner accession : PlatformOwner =
        { Table = "run_platform"
          ParamsTable = "run_platform_params"
          AccessionColumn = accessionColumn
          Accession = accession }

    let private formatDateTime (value: Nullable<DateTime>) : obj =
        if value.HasValue then box (value.Value.ToString("o")) else null

    let private parseDateTime (text: string) : Nullable<DateTime> =
        if isNull text then Nullable() else Nullable(DateTime.Parse text)

    let private insertCore (connection: SqliteConnection) (experimentAccession: string) (run: Run) : unit =
        Sql.execNonQuery
            connection
            "INSERT INTO run \
                (accession, alias, center_name, broker_name, title, experiment_accession, \
                 run_center, run_date, sample_demux_directive) \
             VALUES (@acc, @alias, @cn, @bn, @title, @exp, @rc, @rd, @demux);"
            [
                "@acc", box run.Accession
                "@alias", box run.Alias
                "@cn", box run.CenterName
                "@bn", box run.BrokerName
                "@title", box run.Title
                "@exp", box experimentAccession
                "@rc", box run.RunCenter
                "@rd", formatDateTime run.RunDate
                "@demux", null
            ]
        |> ignore

    let private insertDataBlock (connection: SqliteConnection) (run: Run) : unit =
        match run.DataBlock with
        | null -> ()
        | block ->
            // Files is deferred (schema stores TEXT NULL); only member_name round-trips today.
            Sql.execNonQuery
                connection
                "INSERT INTO run_data_block (run_accession, member_name, files) VALUES (@acc, @member, @files);"
                [
                    "@acc", box run.Accession
                    "@member", box block.MemberName
                    "@files", null
                ]
            |> ignore

    let private insertSpotDescriptor (connection: SqliteConnection) (run: Run) : unit =
        match run.SpotDescriptor with
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
                        "INSERT INTO run_spot_descriptor \
                            (run_accession, read_index, spot_length, item, read_class, read_label, read_type) \
                         VALUES (@acc, @idx, @spotLen, @item, @class, @label, @type);"
                        [
                            "@acc", box run.Accession
                            "@idx", box rs.ReadIndex
                            "@spotLen", spotLength
                            "@item", box "READ_SPEC"
                            "@class", box (string rs.ReadClass)
                            "@label", box rs.ReadLabel
                            "@type", box (string rs.ReadType)
                        ]
                    |> ignore

    /// Persists `run` and every row it deconstructs into. `experimentAccession`
    /// is the parent Experiment's accession (NOT NULL FK on the schema); the
    /// matching Experiment row must already exist.
    let insert (connection: SqliteConnection) (experimentAccession: string) (run: Run) : unit =
        Sql.withTransaction connection (fun _tx ->
            insertCore connection experimentAccession run
            insertDataBlock connection run
            insertSpotDescriptor connection run
            Platforms.write connection (platformOwner run.Accession) run.Platform
            Identifiers.write connection (identifierOwner run.Accession) run.Identifiers
            Attributes.write connection (attributeOwner run.Accession) run.RunAttributes
            Links.write connection (linkOwner run.Accession) run.RunLinks
            References.write connection (experimentRefOwner run.Accession) run.ExperimentRef)

    let private parseEnum<'T when 'T : struct
                                and 'T :> Enum
                                and 'T : (new : unit -> 'T)>
        (text: string)
        : 'T =
        Enum.Parse(typeof<'T>, text) :?> 'T

    let private readDataBlock (connection: SqliteConnection) (accession: string) : RunDataBlock =
        Sql.tryQueryOne
            connection
            "SELECT member_name FROM run_data_block WHERE run_accession = @acc;"
            [ "@acc", box accession ]
            (fun reader ->
                RunDataBlock(MemberName = Sql.readStringOrNull reader 0))
        |> Option.defaultValue null

    let private readSpotDescriptor (connection: SqliteConnection) (accession: string) : SpotDescriptor =
        let rows =
            Sql.queryAll
                connection
                "SELECT read_index, spot_length, read_class, read_label, read_type \
                 FROM run_spot_descriptor WHERE run_accession = @acc ORDER BY read_index;"
                [ "@acc", box accession ]
                (fun reader ->
                    reader.GetInt64(0),
                    (if reader.IsDBNull(1) then Nullable<int64>() else Nullable(reader.GetInt64(1))),
                    Sql.readStringOrNull reader 2,
                    Sql.readStringOrNull reader 3,
                    Sql.readStringOrNull reader 4)
        if List.isEmpty rows then null else

        let descriptor = SpotDescriptor()
        let spec = SpotDescriptorSpotDecodeSpec()
        let firstSpotLength = rows |> List.tryPick (fun (_, len, _, _, _) -> if len.HasValue then Some len.Value else None)
        match firstSpotLength with
        | Some len -> spec.SpotLength <- Nullable(len)
        | None -> ()
        for (idx, _, cls, label, rtype) in rows do
            let rs = SpotDescriptorSpotDecodeSpecReadSpec()
            rs.ReadIndex <- idx
            rs.ReadLabel <- label
            if not (isNull cls) then
                rs.ReadClass <- parseEnum<SpotDescriptorSpotDecodeSpecReadSpecReadClass> cls
            if not (isNull rtype) then
                rs.ReadType <- parseEnum<SpotDescriptorSpotDecodeSpecReadSpecReadType> rtype
            spec.ReadSpec.Add(rs)
        descriptor.SpotDecodeSpec <- spec
        descriptor

    /// Reconstructs a `Run` from its accession by joining every owner table.
    /// Returns `None` when no core row exists.
    let tryGet (connection: SqliteConnection) (accession: string) : Run option =
        let core =
            Sql.tryQueryOne
                connection
                "SELECT alias, center_name, broker_name, title, run_center, run_date FROM run WHERE accession = @acc;"
                [ "@acc", box accession ]
                (fun reader ->
                    Sql.readStringOrNull reader 0,
                    Sql.readStringOrNull reader 1,
                    Sql.readStringOrNull reader 2,
                    Sql.readStringOrNull reader 3,
                    Sql.readStringOrNull reader 4,
                    Sql.readStringOrNull reader 5)
        match core with
        | None -> None
        | Some (alias, centerName, brokerName, title, runCenter, runDate) ->
            let run = Run()
            run.Accession <- accession
            run.Alias <- alias
            run.CenterName <- centerName
            run.BrokerName <- brokerName
            run.Title <- title
            run.RunCenter <- runCenter
            run.RunDate <- parseDateTime runDate
            run.ExperimentRef <- References.read<RunExperimentRef> connection (experimentRefOwner accession)
            run.DataBlock <- readDataBlock connection accession
            run.SpotDescriptor <- readSpotDescriptor connection accession
            run.Platform <- Platforms.read connection (platformOwner accession)
            run.Identifiers <- Identifiers.read connection (identifierOwner accession)
            for attr in Attributes.read connection (attributeOwner accession) do
                run.RunAttributes.Add(attr)
            for link in Links.read connection (linkOwner accession) do
                run.RunLinks.Add(link)
            Some run

    /// Removes the row from `run`; cascades through every owned table.
    let delete (connection: SqliteConnection) (accession: string) : unit =
        Sql.execNonQuery
            connection
            "DELETE FROM run WHERE accession = @acc;"
            [ "@acc", box accession ]
        |> ignore

    /// Lists every Run accession in the database, lexicographically.
    let listAccessions (connection: SqliteConnection) : string seq =
        Sql.queryAll
            connection
            "SELECT accession FROM run ORDER BY accession;"
            []
            (fun reader -> reader.GetString(0))
        :> string seq
