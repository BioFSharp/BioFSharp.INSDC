namespace BioFSharp.INSDC.SQLite.Internal

open System.IO
open System.Reflection
open Microsoft.Data.Sqlite

/// Loads the embedded `insdc_schema.sql` resource and applies it to a freshly
/// opened SQLite connection. The schema file is built into the assembly as an
/// `EmbeddedResource` (see the .fsproj); pulling it from there means callers
/// never need to know where it lives on disk.
module Schema =

    [<Literal>]
    let private resourceName = "BioFSharp.INSDC.SQLite.schema.insdc_schema.sql"

    /// Reads the schema SQL out of the assembly's embedded resources.
    let private readEmbeddedSchema () : string =
        let asm = Assembly.GetExecutingAssembly()
        use stream =
            match asm.GetManifestResourceStream(resourceName) with
            | null ->
                let available = asm.GetManifestResourceNames() |> String.concat ", "
                failwithf
                    "Embedded resource '%s' not found in %s. Available resources: %s"
                    resourceName
                    asm.FullName
                    available
            | s -> s
        use reader = new StreamReader(stream)
        reader.ReadToEnd()

    /// Applies the bundled schema (CREATE TABLE / CREATE INDEX statements) to
    /// `connection`. Intended for a fresh database; running it against a DB
    /// that already has the tables will throw.
    let init (connection: SqliteConnection) : unit =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- readEmbeddedSchema ()
        cmd.ExecuteNonQuery() |> ignore
