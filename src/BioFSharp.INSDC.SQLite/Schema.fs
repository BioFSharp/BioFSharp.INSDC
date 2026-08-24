namespace BioFSharp.INSDC.SQLite

open Microsoft.Data.Sqlite

/// Controls whether SQLite validates declared foreign keys on a connection.
type ForeignKeyMode =
    /// Enforce all declared relationships. This is the default for ordinary
    /// store use and catches missing hard parents.
    | Enforce
    /// Permit the crawler's documented soft references to dangle. ENA may name
    /// the same sample by an SRA accession in one record and a BioSample
    /// accession in another, and partial crawls intentionally store either side.
    | AllowCrawlerSoftReferences

/// Public schema-version and migration surface for the SQLite store.
module Schema =

    /// The newest schema version understood by this package.
    let currentVersion = Internal.Schema.currentVersion

    /// Returns the database's `PRAGMA user_version` value.
    let version (connection: SqliteConnection) : int =
        Internal.Schema.getVersion connection

    /// Sets and verifies foreign-key enforcement for `connection`. Change the
    /// mode only outside a transaction; normal callers should keep `Enforce`.
    let setForeignKeyMode (connection: SqliteConnection) (mode: ForeignKeyMode) : unit =
        match mode with
        | Enforce -> Internal.Sql.setForeignKeys true connection
        | AllowCrawlerSoftReferences -> Internal.Sql.setForeignKeys false connection

    /// Initializes a new database or applies every ordered forward migration to
    /// an existing supported database. The operation is idempotent and each
    /// migration is transactional. Foreign keys are enabled on return.
    let init (connection: SqliteConnection) : unit =
        Internal.Sql.setForeignKeys true connection
        Internal.Schema.migrate connection
