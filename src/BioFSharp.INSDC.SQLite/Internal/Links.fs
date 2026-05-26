namespace BioFSharp.INSDC.SQLite.Internal

open System
open Microsoft.Data.Sqlite
open BioFSharp.FileFormats.INSDC

/// Identity of the `{entity}_links` table for one owning entity.
type LinkOwner = {
    Table: string
    AccessionColumn: string
    Accession: string
}

/// Persists / hydrates the `Link` collection that every INSDC entity carries.
/// `Link` is a DU in spirit (exactly one of `UrlLink` / `XrefLink` / `EntrezLink`
/// is set per row); the schema mirrors that with a `link_kind` discriminator
/// column and only populates the subset of payload columns relevant to that kind.
module Links =

    [<Literal>]
    let private kindUrl = "URL"

    [<Literal>]
    let private kindXref = "XREF"

    [<Literal>]
    let private kindEntrez = "ENTREZ"

    let private insertRow
        (connection: SqliteConnection)
        (owner: LinkOwner)
        (ordinal: int)
        (kind: string)
        (label: string)
        (url: string)
        (db: string)
        (id: string)
        (query: string)
        : unit =
        let sql =
            sprintf
                "INSERT INTO %s (%s, ordinal, link_kind, label, url, db, id, query) VALUES (@acc, @ordinal, @kind, @label, @url, @db, @id, @query);"
                owner.Table
                owner.AccessionColumn
        Sql.execNonQuery
            connection
            sql
            [
                "@acc", box owner.Accession
                "@ordinal", box ordinal
                "@kind", box kind
                "@label", box label
                "@url", box url
                "@db", box db
                "@id", box id
                "@query", box query
            ]
        |> ignore

    /// Writes each `Link`'s populated DU case to the owner's links table.
    /// The kind is derived from which sub-element is non-null on the Link
    /// instance; a Link with multiple sub-elements set would be invalid by
    /// the XSD but is still handled deterministically (URL → XREF → ENTREZ).
    let write
        (connection: SqliteConnection)
        (owner: LinkOwner)
        (links: System.Collections.Generic.IEnumerable<Link>)
        : unit =
        if isNull links then () else
        links
        |> Seq.iteri (fun i link ->
            if not (isNull link.UrlLink) then
                let u = link.UrlLink
                insertRow connection owner i kindUrl u.Label u.UrlProperty null null null
            elif not (isNull link.XrefLink) then
                let x = link.XrefLink
                insertRow connection owner i kindXref x.Label null x.Db x.Id null
            elif not (isNull link.EntrezLink) then
                let e = link.EntrezLink
                let idText = if e.Id.HasValue then string e.Id.Value else null
                insertRow connection owner i kindEntrez e.Label null e.Db idText e.Query
            else
                failwithf
                    "Link at ordinal %d in %s for %s='%s' has no URL_LINK / XREF_LINK / ENTREZ_LINK set"
                    i owner.Table owner.AccessionColumn owner.Accession)

    /// Reads all link rows in ordinal order and reconstructs `Link` instances
    /// with the appropriate DU case populated. Returns an empty list when
    /// none exist.
    let read (connection: SqliteConnection) (owner: LinkOwner) : Link list =
        let sql =
            sprintf
                "SELECT link_kind, label, url, db, id, query FROM %s WHERE %s = @acc ORDER BY ordinal;"
                owner.Table
                owner.AccessionColumn
        Sql.queryAll
            connection
            sql
            [ "@acc", box owner.Accession ]
            (fun reader ->
                let kind = reader.GetString(0)
                let label = Sql.readStringOrNull reader 1
                let url = Sql.readStringOrNull reader 2
                let db = Sql.readStringOrNull reader 3
                let id = Sql.readStringOrNull reader 4
                let query = Sql.readStringOrNull reader 5
                let link = Link()
                match kind with
                | k when k = kindUrl ->
                    link.UrlLink <- Url(Label = label, UrlProperty = url)
                | k when k = kindXref ->
                    link.XrefLink <- XRef(Label = label, Db = db, Id = id)
                | k when k = kindEntrez ->
                    let entrez = LinkEntrezLink(Label = label, Db = db, Query = query)
                    if not (isNull id) then
                        entrez.Id <- Nullable<int64>(Int64.Parse(id))
                    link.EntrezLink <- entrez
                | other ->
                    failwithf
                        "Unexpected link_kind '%s' in %s for %s='%s'"
                        other owner.Table owner.AccessionColumn owner.Accession
                link)
