namespace BioFSharp.INSDC.SQLite

open Microsoft.Data.Sqlite
open BioFSharp.FileFormats.INSDC
open BioFSharp.INSDC.SQLite.Internal

/// SQLite persistence for `BioSample`. The taxonomic identity sits in its own
/// child table (`biosample_name`); everything else follows the standard
/// identifiers/attributes/links pattern.
module BioSample =

    [<Literal>]
    let private accessionColumn = "biosample_accession"

    let private identifierOwner accession : IdentifierOwner =
        { Table = "biosample_identifiers"; AccessionColumn = accessionColumn; Accession = accession }

    let private attributeOwner accession : AttributeOwner =
        { Table = "biosample_attributes"; AccessionColumn = accessionColumn; Accession = accession }

    let private linkOwner accession : LinkOwner =
        { Table = "biosample_links"; AccessionColumn = accessionColumn; Accession = accession }

    let private insertCore (connection: SqliteConnection) (sample: BioSample) : unit =
        Sql.execNonQuery
            connection
            "INSERT INTO biosample (accession, alias, center_name, broker_name, title, description) \
             VALUES (@acc, @alias, @cn, @bn, @title, @desc);"
            [
                "@acc", box sample.Accession
                "@alias", box sample.Alias
                "@cn", box sample.CenterName
                "@bn", box sample.BrokerName
                "@title", box sample.Title
                "@desc", box sample.Description
            ]
        |> ignore

    let private insertSampleName (connection: SqliteConnection) (sample: BioSample) : unit =
        match sample.SampleName with
        | null -> ()
        | name ->
            Sql.execNonQuery
                connection
                "INSERT INTO biosample_name (biosample_accession, taxon_id, scientific_name, common_name, display_name) \
                 VALUES (@acc, @tid, @sci, @common, @display);"
                [
                    "@acc", box sample.Accession
                    "@tid", box name.TaxonId
                    "@sci", box name.ScientificName
                    "@common", box name.CommonName
                    "@display", box name.DisplayName
                ]
            |> ignore

    /// Persists `sample` and every row it deconstructs into.
    let insert (connection: SqliteConnection) (sample: BioSample) : unit =
        Sql.withTransaction connection (fun _tx ->
            insertCore connection sample
            insertSampleName connection sample
            Identifiers.write connection (identifierOwner sample.Accession) sample.Identifiers
            Attributes.write connection (attributeOwner sample.Accession) sample.SampleAttributes
            Links.write connection (linkOwner sample.Accession) sample.SampleLinks)

    let private readSampleName (connection: SqliteConnection) (accession: string) : BioSampleName =
        Sql.tryQueryOne
            connection
            "SELECT taxon_id, scientific_name, common_name, display_name \
             FROM biosample_name WHERE biosample_accession = @acc;"
            [ "@acc", box accession ]
            (fun reader ->
                BioSampleName(
                    TaxonId = reader.GetInt32(0),
                    ScientificName = Sql.readStringOrNull reader 1,
                    CommonName = Sql.readStringOrNull reader 2,
                    DisplayName = Sql.readStringOrNull reader 3))
        |> Option.defaultValue null

    /// Reconstructs a `BioSample` from its accession by joining every owner
    /// table. Returns `None` when no core row exists.
    let tryGet (connection: SqliteConnection) (accession: string) : BioSample option =
        let core =
            Sql.tryQueryOne
                connection
                "SELECT alias, center_name, broker_name, title, description \
                 FROM biosample WHERE accession = @acc;"
                [ "@acc", box accession ]
                (fun reader ->
                    Sql.readStringOrNull reader 0,
                    Sql.readStringOrNull reader 1,
                    Sql.readStringOrNull reader 2,
                    Sql.readStringOrNull reader 3,
                    Sql.readStringOrNull reader 4)
        match core with
        | None -> None
        | Some (alias, centerName, brokerName, title, description) ->
            let sample = BioSample()
            sample.Accession <- accession
            sample.Alias <- alias
            sample.CenterName <- centerName
            sample.BrokerName <- brokerName
            sample.Title <- title
            sample.Description <- description
            sample.SampleName <- readSampleName connection accession
            sample.Identifiers <- Identifiers.read connection (identifierOwner accession)
            for attr in Attributes.read connection (attributeOwner accession) do
                sample.SampleAttributes.Add(attr)
            for link in Links.read connection (linkOwner accession) do
                sample.SampleLinks.Add(link)
            Some sample

    /// Removes the row from `biosample`; cascades through every owned table.
    let delete (connection: SqliteConnection) (accession: string) : unit =
        Sql.withTransaction connection (fun _ ->
            Sql.execNonQuery
                connection
                "DELETE FROM biosample WHERE accession = @acc;"
                [ "@acc", box accession ]
            |> ignore)

    /// Lists every BioSample accession in the database, lexicographically.
    let listAccessions (connection: SqliteConnection) : string seq =
        Sql.queryAll
            connection
            "SELECT accession FROM biosample ORDER BY accession;"
            []
            (fun reader -> reader.GetString(0))
        :> string seq
