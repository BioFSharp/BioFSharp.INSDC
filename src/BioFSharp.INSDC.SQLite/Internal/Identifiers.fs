namespace BioFSharp.INSDC.SQLite.Internal

open Microsoft.Data.Sqlite
open BioFSharp.FileFormats.INSDC

/// Identity of the row family that holds an `Identifier` composite — five
/// entity tables (`{entity}_identifiers`) and three reference-nested tables
/// (`{owner}_{ref}_identifiers`) all share the same column shape, so the read
/// and write paths are parameterized over the table to avoid eight near-
/// duplicate modules.
type IdentifierOwner = {
    /// Fully-qualified SQLite table name, e.g. `"bioproject_identifiers"`.
    Table: string
    /// FK column referencing the owning entity, e.g. `"bioproject_accession"`.
    AccessionColumn: string
    /// Accession value of the owning row that all written identifiers belong to.
    Accession: string
}

/// Persists / hydrates the `Identifier` composite (PrimaryId, SecondaryId[],
/// ExternalId[], SubmitterId, Uuid[]) against a unified per-owner identifier
/// table. The DB shape is one row per identifier kind+ordinal pair; the kind
/// discriminator restores which slot in the F# composite a row populates.
module Identifiers =

    [<Literal>]
    let private kindPrimary = "PRIMARY"

    [<Literal>]
    let private kindSecondary = "SECONDARY"

    [<Literal>]
    let private kindExternal = "EXTERNAL"

    [<Literal>]
    let private kindSubmitter = "SUBMITTER"

    [<Literal>]
    let private kindUuid = "UUID"

    let private insertRow
        (connection: SqliteConnection)
        (owner: IdentifierOwner)
        (kind: string)
        (ordinal: int)
        (value: string)
        (label: string)
        (ns: string)
        : unit =
        let sql =
            sprintf
                "INSERT INTO %s (%s, kind, ordinal, value, label, namespace) VALUES (@acc, @kind, @ordinal, @value, @label, @namespace);"
                owner.Table
                owner.AccessionColumn
        Sql.execNonQuery
            connection
            sql
            [
                "@acc", box owner.Accession
                "@kind", box kind
                "@ordinal", box ordinal
                "@value", box value
                "@label", box label
                "@namespace", box ns
            ]
        |> ignore

    /// Writes every populated identifier on `identifiers` into the owner's
    /// identifier table. Null `identifiers` is treated as "nothing to write",
    /// matching the XSD where the whole IDENTIFIERS element is optional.
    let write (connection: SqliteConnection) (owner: IdentifierOwner) (identifiers: Identifier) : unit =
        if isNull identifiers then () else

        match identifiers.PrimaryId with
        | null -> ()
        | n -> insertRow connection owner kindPrimary 0 n.Value n.Label null

        if not (isNull identifiers.SecondaryId) then
            identifiers.SecondaryId
            |> Seq.iteri (fun i n ->
                insertRow connection owner kindSecondary i n.Value n.Label null)

        if not (isNull identifiers.ExternalId) then
            identifiers.ExternalId
            |> Seq.iteri (fun i qn ->
                insertRow connection owner kindExternal i qn.Value qn.Label qn.Namespace)

        match identifiers.SubmitterId with
        | null -> ()
        | qn -> insertRow connection owner kindSubmitter 0 qn.Value qn.Label qn.Namespace

        if not (isNull identifiers.Uuid) then
            identifiers.Uuid
            |> Seq.iteri (fun i n ->
                insertRow connection owner kindUuid i n.Value n.Label null)

    /// Reads every row in the owner's identifier table and reassembles the
    /// F# `Identifier` composite. Returns `null` when no rows exist — the
    /// generated INSDC types keep the IDENTIFIERS element as an optional
    /// reference, and CLR-`null` is what the XML serializer round-trips to.
    let read (connection: SqliteConnection) (owner: IdentifierOwner) : Identifier =
        let sql =
            sprintf
                "SELECT kind, ordinal, value, label, namespace FROM %s WHERE %s = @acc ORDER BY kind, ordinal;"
                owner.Table
                owner.AccessionColumn
        let rows =
            Sql.queryAll
                connection
                sql
                [ "@acc", box owner.Accession ]
                (fun reader ->
                    let kind = reader.GetString(0)
                    let value = reader.GetString(2)
                    let label = Sql.readStringOrNull reader 3
                    let ns = Sql.readStringOrNull reader 4
                    kind, value, label, ns)

        if List.isEmpty rows then null else

        let identifiers = Identifier()
        for (kind, value, label, ns) in rows do
            match kind with
            | k when k = kindPrimary ->
                identifiers.PrimaryId <- Name(Value = value, Label = label)
            | k when k = kindSecondary ->
                identifiers.SecondaryId.Add(Name(Value = value, Label = label))
            | k when k = kindExternal ->
                identifiers.ExternalId.Add(QualifiedName(Value = value, Label = label, Namespace = ns))
            | k when k = kindSubmitter ->
                identifiers.SubmitterId <- QualifiedName(Value = value, Label = label, Namespace = ns)
            | k when k = kindUuid ->
                identifiers.Uuid.Add(Name(Value = value, Label = label))
            | other ->
                failwithf "Unexpected identifier kind '%s' in %s for %s='%s'" other owner.Table owner.AccessionColumn owner.Accession
        identifiers
