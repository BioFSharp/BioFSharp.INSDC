namespace BioFSharp.INSDC.SQLite.Internal

open Microsoft.Data.Sqlite
open BioFSharp.FileFormats.INSDC

/// Identity of one `RefObject` row family: the reference table itself plus
/// the sibling `_identifiers` table for its nested `RefObject.Identifiers`.
/// One value of this record describes everything the helper needs to write or
/// read either Experiment.StudyRef, Experiment.SampleDescriptor, or
/// Run.ExperimentRef without baking in entity-specific SQL.
type RefObjectOwner = {
    /// Reference table, e.g. `"experiment_study_ref"`.
    Table: string
    /// Sibling identifier table, e.g. `"experiment_study_ref_identifiers"`.
    IdentifiersTable: string
    /// FK column referencing the owning entity (same name in both tables).
    AccessionColumn: string
    /// Accession of the owning entity (the experiment or run).
    Accession: string
}

/// Persists / hydrates a `RefObject` (and its nested `Identifier` collection)
/// against the dedicated reference + identifiers table pair. The generated
/// types use subclasses (ExperimentStudyRef, BioSampleDescriptor,
/// RunExperimentRef) all derived from RefObject; the read path is generic
/// over the concrete subclass so callers can request the right type directly.
module References =

    /// Writes the reference row and its nested identifiers. Null `refObject`
    /// is a no-op, matching the XSD's optional reference elements.
    let write
        (connection: SqliteConnection)
        (owner: RefObjectOwner)
        (refObject: RefObject)
        : unit =
        if isNull refObject then () else
        let sql =
            sprintf
                "INSERT INTO %s (%s, accession, refname, refcenter) VALUES (@acc, @target, @refname, @refcenter);"
                owner.Table
                owner.AccessionColumn
        Sql.execNonQuery
            connection
            sql
            [
                "@acc", box owner.Accession
                "@target", box refObject.Accession
                "@refname", box refObject.Refname
                "@refcenter", box refObject.Refcenter
            ]
        |> ignore
        Identifiers.write
            connection
            { Table = owner.IdentifiersTable
              AccessionColumn = owner.AccessionColumn
              Accession = owner.Accession }
            refObject.Identifiers

    /// Reads the reference row (if present) and rehydrates a concrete
    /// `RefObject` subclass `'T`. Returns `null` when no row exists so the
    /// caller can leave the parent entity's nullable property unset.
    /// The new()-constrained generic lets the same helper produce a typed
    /// ExperimentStudyRef, BioSampleDescriptor, or RunExperimentRef.
    let inline read<'T when 'T :> RefObject and 'T : (new : unit -> 'T)>
        (connection: SqliteConnection)
        (owner: RefObjectOwner)
        : 'T =
        let sql =
            sprintf
                "SELECT accession, refname, refcenter FROM %s WHERE %s = @acc;"
                owner.Table
                owner.AccessionColumn
        let row =
            Sql.tryQueryOne
                connection
                sql
                [ "@acc", box owner.Accession ]
                (fun reader ->
                    Sql.readStringOrNull reader 0,
                    Sql.readStringOrNull reader 1,
                    Sql.readStringOrNull reader 2)
        match row with
        | None -> Unchecked.defaultof<'T>
        | Some (targetAccession, refname, refcenter) ->
            let refObject = new 'T()
            refObject.Accession <- targetAccession
            refObject.Refname <- refname
            refObject.Refcenter <- refcenter
            refObject.Identifiers <-
                Identifiers.read
                    connection
                    { Table = owner.IdentifiersTable
                      AccessionColumn = owner.AccessionColumn
                      Accession = owner.Accession }
            refObject
