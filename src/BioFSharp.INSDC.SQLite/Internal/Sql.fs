namespace BioFSharp.INSDC.SQLite.Internal

open System
open Microsoft.Data.Sqlite

/// Thin helpers over `Microsoft.Data.Sqlite` — open with FK enforcement on,
/// run statements with named parameter binding, and execute work inside an
/// explicit transaction.
module Sql =

    /// Marshals an arbitrary CLR value into the form `SqliteCommand` expects:
    /// CLR `null` becomes a SQL NULL, everything else passes through as-is.
    let private toParameterValue (value: obj) : obj =
        if isNull value then DBNull.Value :> obj else value

    /// Reads a TEXT column as a plain string, or `null` when the column is
    /// NULL. The generated INSDC C# types use plain `string` properties for
    /// optional fields, so passing back CLR `null` matches the post-XML-
    /// deserialization shape.
    let readStringOrNull (reader: SqliteDataReader) (ordinal: int) : string =
        if reader.IsDBNull(ordinal) then null else reader.GetString(ordinal)

    /// Opens a SQLite connection at `connectionString` and enables foreign-key
    /// enforcement on it. `PRAGMA foreign_keys` is connection-scoped in SQLite,
    /// so it must be set after each open.
    let openConnection (connectionString: string) : SqliteConnection =
        let connection = new SqliteConnection(connectionString)
        connection.Open()
        use cmd = connection.CreateCommand()
        cmd.CommandText <- "PRAGMA foreign_keys = ON;"
        cmd.ExecuteNonQuery() |> ignore
        connection

    let private addParameters (cmd: SqliteCommand) (parameters: (string * obj) seq) : unit =
        for (name, value) in parameters do
            let p = cmd.CreateParameter()
            p.ParameterName <- name
            p.Value <- toParameterValue value
            cmd.Parameters.Add(p) |> ignore

    /// Executes a non-query statement (INSERT / UPDATE / DELETE / DDL) with
    /// the given parameters bound by `@name`.
    let execNonQuery (connection: SqliteConnection) (sql: string) (parameters: (string * obj) seq) : int =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        addParameters cmd parameters
        cmd.ExecuteNonQuery()

    /// Executes a query, projects every row through `project`, and returns the
    /// projections eagerly (the reader is fully drained before the function
    /// returns so the command's lifetime never escapes the call site).
    let queryAll<'a> (connection: SqliteConnection) (sql: string) (parameters: (string * obj) seq) (project: SqliteDataReader -> 'a) : 'a list =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        addParameters cmd parameters
        use reader = cmd.ExecuteReader()
        let acc = ResizeArray<'a>()
        while reader.Read() do
            acc.Add(project reader)
        List.ofSeq acc

    /// Executes a query and returns the first row's projection, or `None` if
    /// no row matched. The reader is closed before the function returns.
    let tryQueryOne<'a> (connection: SqliteConnection) (sql: string) (parameters: (string * obj) seq) (project: SqliteDataReader -> 'a) : 'a option =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        addParameters cmd parameters
        use reader = cmd.ExecuteReader()
        if reader.Read() then Some (project reader) else None

    /// The transaction currently active on `connection`, or `null` if none.
    /// `SqliteConnection` exposes no public accessor for it, but a freshly
    /// created command auto-binds its `Transaction` to the connection's active
    /// one (and leaves it `null` when there is none), so a throwaway command
    /// reveals it.
    let private activeTransaction (connection: SqliteConnection) : SqliteTransaction =
        use probe = connection.CreateCommand()
        probe.Transaction

    /// Runs `work` inside an explicit transaction; commits on success, rolls
    /// back on any exception, and rethrows. Use this around the per-entity
    /// `insert` calls so a partial deconstruction never leaves orphan rows.
    ///
    /// Reentrant: SQLite has no nested transactions (a second `BeginTransaction`
    /// throws), so when a transaction is already active on `connection` this
    /// joins it — running `work` inline and leaving the commit/rollback to the
    /// outermost owner. That lets a bulk caller wrap many per-entity `insert`s
    /// (each of which calls `withTransaction` itself) in one surrounding
    /// transaction, so the whole batch commits once instead of fsync-ing per
    /// record.
    let withTransaction (connection: SqliteConnection) (work: SqliteTransaction -> 'a) : 'a =
        let active = activeTransaction connection

        if isNull active then
            use tx = connection.BeginTransaction()
            try
                let result = work tx
                tx.Commit()
                result
            with _ ->
                tx.Rollback()
                reraise ()
        else
            work active
