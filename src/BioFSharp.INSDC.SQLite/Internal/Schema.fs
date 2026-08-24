namespace BioFSharp.INSDC.SQLite.Internal

open System
open System.Data
open System.IO
open System.Reflection
open Microsoft.Data.Sqlite

/// Ordered, forward-only migrations for the embedded INSDC SQLite schema.
/// Version 1 is the original normalized schema; later versions are individual
/// resources applied transactionally and recorded through `PRAGMA user_version`.
module internal Schema =

    [<Literal>]
    let currentVersion = 2

    [<Literal>]
    let private versionOneResource = "BioFSharp.INSDC.SQLite.schema.insdc_schema.sql"

    let private migrations =
        [ 2,
          "BioFSharp.INSDC.SQLite.schema.migrations.002_schema_history.sql" ]

    let private readEmbeddedResource (resourceName: string) : string =
        let assembly = Assembly.GetExecutingAssembly()

        use stream =
            match assembly.GetManifestResourceStream(resourceName) with
            | null ->
                let available = assembly.GetManifestResourceNames() |> String.concat ", "
                failwithf
                    "Embedded resource '%s' not found in %s. Available resources: %s"
                    resourceName
                    assembly.FullName
                    available
            | value -> value

        use reader = new StreamReader(stream)
        reader.ReadToEnd()

    let getVersion (connection: SqliteConnection) : int =
        use command = connection.CreateCommand()
        command.CommandText <- "PRAGMA user_version;"
        Convert.ToInt32(command.ExecuteScalar())

    let private setVersion (connection: SqliteConnection) version =
        use command = connection.CreateCommand()
        command.CommandText <- sprintf "PRAGMA user_version = %d;" version
        command.ExecuteNonQuery() |> ignore

    let private userTables (connection: SqliteConnection) =
        Sql.queryAll
            connection
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"
            []
            (fun reader -> reader.GetString 0)
        |> Set.ofList

    let private applySql version sql (connection: SqliteConnection) =
        Sql.withTransaction connection (fun _ ->
            use command = connection.CreateCommand()
            command.CommandText <- sql
            command.ExecuteNonQuery() |> ignore
            setVersion connection version)

    let private recognizeVersionOne (tables: Set<string>) =
        let required =
            set [ "bioproject"; "study"; "biosample"; "experiment"; "run"; "accession_relations" ]

        Set.isSubset required tables

    /// Initializes an empty database or migrates a recognized older schema to
    /// `currentVersion`. Repeated calls are idempotent. Unknown/future schemas
    /// fail rather than being guessed at or silently overwritten.
    let migrate (connection: SqliteConnection) : unit =
        if connection.State <> ConnectionState.Open then
            invalidArg (nameof connection) "The SQLite connection must be open before schema initialization."

        let initialVersion = getVersion connection

        if initialVersion > currentVersion then
            failwithf
                "Database schema version %d is newer than this library supports (%d)."
                initialVersion
                currentVersion

        let mutable version = initialVersion

        if version = 0 then
            let tables = userTables connection

            if Set.isEmpty tables then
                applySql
                    1
                    (readEmbeddedResource versionOneResource)
                    connection
                version <- 1
            elif recognizeVersionOne tables then
                // Databases produced before versioning shipped already have the
                // exact v1 table shape. Adopt them without replaying CREATEs.
                Sql.withTransaction connection (fun _ -> setVersion connection 1)
                version <- 1
            else
                failwithf
                    "Cannot initialize an unversioned, non-empty SQLite database. Found tables: %s"
                    (tables |> String.concat ", ")

        for migrationVersion, resourceName in migrations do
            if migrationVersion > version then
                applySql
                    migrationVersion
                    (readEmbeddedResource resourceName)
                    connection
                version <- migrationVersion

        if version <> currentVersion then
            failwithf
                "No ordered migration path from schema version %d to %d."
                initialVersion
                currentVersion
