namespace BioFSharp.INSDC.SQLite

open Microsoft.Data.Sqlite

/// Public surface for applying the bundled SQLite schema to a connection.
/// Internal helpers live in `BioFSharp.INSDC.SQLite.Internal`; this module is
/// the only entry point callers should need to bootstrap a fresh database.
module Schema =

    /// Applies the bundled INSDC schema (entity tables, identifier/attribute/
    /// link tables, reference + platform tables, FK indexes) to `connection`.
    /// Intended for a fresh database; running against an already-initialized
    /// DB will throw on the duplicate CREATE TABLE.
    let init (connection: SqliteConnection) : unit =
        Internal.Schema.init connection
