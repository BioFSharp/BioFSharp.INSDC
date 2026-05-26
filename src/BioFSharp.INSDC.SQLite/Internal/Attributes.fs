namespace BioFSharp.INSDC.SQLite.Internal

open Microsoft.Data.Sqlite
open BioFSharp.FileFormats.INSDC

/// Identity of the `{entity}_attributes` table for one owning entity.
type AttributeOwner = {
    Table: string
    AccessionColumn: string
    Accession: string
}

/// Persists / hydrates the tag/value/units attribute collection that every
/// INSDC entity carries. The schema uses one row per attribute keyed by
/// `(accession, ordinal)`; ordinal preserves the original XML document order
/// so XML → SQLite → XML round trips don't reshuffle.
module Attributes =

    /// Inserts each `attribute` into the owner's attributes table, assigning
    /// ordinals in iteration order. Null or empty input writes nothing.
    let write
        (connection: SqliteConnection)
        (owner: AttributeOwner)
        (attributes: System.Collections.Generic.IEnumerable<Attribute>)
        : unit =
        if isNull attributes then () else
        let sql =
            sprintf
                "INSERT INTO %s (%s, ordinal, tag, value, units) VALUES (@acc, @ordinal, @tag, @value, @units);"
                owner.Table
                owner.AccessionColumn
        attributes
        |> Seq.iteri (fun i attr ->
            Sql.execNonQuery
                connection
                sql
                [
                    "@acc", box owner.Accession
                    "@ordinal", box i
                    "@tag", box attr.Tag
                    "@value", box attr.Value
                    "@units", box attr.Units
                ]
            |> ignore)

    /// Reads all rows in the owner's attributes table in ordinal order and
    /// builds matching `Attribute` instances. Returns an empty list when none
    /// exist; callers populate their parent's attribute collection with `Add`.
    let read (connection: SqliteConnection) (owner: AttributeOwner) : Attribute list =
        let sql =
            sprintf
                "SELECT tag, value, units FROM %s WHERE %s = @acc ORDER BY ordinal;"
                owner.Table
                owner.AccessionColumn
        Sql.queryAll
            connection
            sql
            [ "@acc", box owner.Accession ]
            (fun reader ->
                Attribute(
                    Tag = reader.GetString(0),
                    Value = Sql.readStringOrNull reader 1,
                    Units = Sql.readStringOrNull reader 2))
