namespace BioFSharp.INSDC.SQLite

open System
open Microsoft.Data.Sqlite
open BioFSharp.FileFormats.INSDC
open BioFSharp.INSDC.SQLite.Internal

/// SQLite persistence for `Study`. The Study row carries an optional FK to its
/// parent BioProject; that linkage isn't on the F# type itself, so callers pass
/// it explicitly (or leave it `null` for a standalone study).
module Study =

    [<Literal>]
    let private accessionColumn = "study_accession"

    let private identifierOwner accession : IdentifierOwner =
        { Table = "study_identifiers"; AccessionColumn = accessionColumn; Accession = accession }

    let private attributeOwner accession : AttributeOwner =
        { Table = "study_attributes"; AccessionColumn = accessionColumn; Accession = accession }

    let private linkOwner accession : LinkOwner =
        { Table = "study_links"; AccessionColumn = accessionColumn; Accession = accession }

    let private parseNullableLong (text: string) : Nullable<int64> =
        if isNull text then Nullable() else Nullable(Int64.Parse text)

    let private formatNullableLong (value: Nullable<int64>) : obj =
        if value.HasValue then box (string value.Value) else null

    let private insertCore (connection: SqliteConnection) (bioProjectAccession: string) (study: Study) : unit =
        Sql.execNonQuery
            connection
            "INSERT INTO study (accession, alias, center_name, broker_name, bioproject_accession) \
             VALUES (@acc, @alias, @cn, @bn, @bp);"
            [
                "@acc", box study.Accession
                "@alias", box study.Alias
                "@cn", box study.CenterName
                "@bn", box study.BrokerName
                "@bp", box bioProjectAccession
            ]
        |> ignore

    let private insertDescriptor (connection: SqliteConnection) (study: Study) : unit =
        match study.Descriptor with
        | null -> ()
        | d ->
            let existing, newType =
                match d.Study with
                | null -> null, null
                | st ->
                    let existing = string st.ExistingStudyType
                    existing, st.NewStudyType
            Sql.execNonQuery
                connection
                "INSERT INTO study_descriptor \
                    (study_accession, study_title, existing_study_type, new_study_type, study_abstract, \
                     center_name, descriptor_center_name, center_project_name, project_id, study_description) \
                 VALUES (@acc, @title, @existing, @new, @abs, @cn, @dcn, @cpn, @pid, @desc);"
                [
                    "@acc", box study.Accession
                    "@title", box d.StudyTitle
                    "@existing", box existing
                    "@new", box newType
                    "@abs", box d.StudyAbstract
                    "@cn", box d.CenterName
                    "@dcn", null // descriptor_center_name has no counterpart on the F# type
                    "@cpn", box d.CenterProjectName
                    "@pid", formatNullableLong d.ProjectId
                    "@desc", box d.StudyDescription
                ]
            |> ignore
            if not (isNull d.RelatedStudies) then
                d.RelatedStudies
                |> Seq.iteri (fun i related ->
                    let db, id, label =
                        match related.RelatedLink with
                        | null -> null, null, null
                        | xref -> xref.Db, xref.Id, xref.Label
                    Sql.execNonQuery
                        connection
                        "INSERT INTO study_related_studies \
                            (study_accession, ordinal, related_link_db, related_link_id, related_link_label, is_primary) \
                         VALUES (@acc, @ordinal, @db, @id, @label, @isPrim);"
                        [
                            "@acc", box study.Accession
                            "@ordinal", box i
                            "@db", box db
                            "@id", box id
                            "@label", box label
                            "@isPrim", box (if related.IsPrimary then 1 else 0)
                        ]
                    |> ignore)

    /// Persists `study` and every row it deconstructs into. `bioProjectAccession`
    /// links this study to a parent BioProject when known; pass `null` for a
    /// standalone study (the FK column is NULLable).
    let insert (connection: SqliteConnection) (bioProjectAccession: string) (study: Study) : unit =
        Sql.withTransaction connection (fun _tx ->
            insertCore connection bioProjectAccession study
            insertDescriptor connection study
            Identifiers.write connection (identifierOwner study.Accession) study.Identifiers
            Attributes.write connection (attributeOwner study.Accession) study.StudyAttributes
            Links.write connection (linkOwner study.Accession) study.StudyLinks)

    let private readDescriptor (connection: SqliteConnection) (accession: string) : StudyDescriptor =
        let core =
            Sql.tryQueryOne
                connection
                "SELECT study_title, existing_study_type, new_study_type, study_abstract, \
                        center_name, center_project_name, project_id, study_description \
                 FROM study_descriptor WHERE study_accession = @acc;"
                [ "@acc", box accession ]
                (fun reader ->
                    Sql.readStringOrNull reader 0,
                    Sql.readStringOrNull reader 1,
                    Sql.readStringOrNull reader 2,
                    Sql.readStringOrNull reader 3,
                    Sql.readStringOrNull reader 4,
                    Sql.readStringOrNull reader 5,
                    Sql.readStringOrNull reader 6,
                    Sql.readStringOrNull reader 7)
        let relatedRows =
            Sql.queryAll
                connection
                "SELECT related_link_db, related_link_id, related_link_label, is_primary \
                 FROM study_related_studies WHERE study_accession = @acc ORDER BY ordinal;"
                [ "@acc", box accession ]
                (fun reader ->
                    Sql.readStringOrNull reader 0,
                    Sql.readStringOrNull reader 1,
                    Sql.readStringOrNull reader 2,
                    reader.GetInt64(3) = 1L)
        match core, relatedRows with
        | None, [] -> null
        | _ ->
            let descriptor = StudyDescriptor()
            match core with
            | Some (title, existing, newType, abs', cn, cpn, pid, desc) ->
                descriptor.StudyTitle <- title
                if not (isNull existing) || not (isNull newType) then
                    let studyType = StudyDescriptorStudyType()
                    if not (isNull existing) then
                        studyType.ExistingStudyType <-
                            Enum.Parse(typeof<StudyDescriptorStudyTypeExistingStudyType>, existing)
                            :?> StudyDescriptorStudyTypeExistingStudyType
                    studyType.NewStudyType <- newType
                    descriptor.Study <- studyType
                descriptor.StudyAbstract <- abs'
                descriptor.CenterName <- cn
                descriptor.CenterProjectName <- cpn
                descriptor.ProjectId <- parseNullableLong pid
                descriptor.StudyDescription <- desc
            | None -> ()
            for (db, id, label, isPrim) in relatedRows do
                let item = StudyDescriptorRelatedStudiesRelatedStudy()
                if not (isNull db) || not (isNull id) || not (isNull label) then
                    item.RelatedLink <- XRef(Db = db, Id = id, Label = label)
                item.IsPrimary <- isPrim
                descriptor.RelatedStudies.Add(item)
            descriptor

    /// Reconstructs a `Study` from its accession by joining every owner table.
    /// Returns `None` when no core row exists.
    let tryGet (connection: SqliteConnection) (accession: string) : Study option =
        let core =
            Sql.tryQueryOne
                connection
                "SELECT alias, center_name, broker_name FROM study WHERE accession = @acc;"
                [ "@acc", box accession ]
                (fun reader ->
                    Sql.readStringOrNull reader 0,
                    Sql.readStringOrNull reader 1,
                    Sql.readStringOrNull reader 2)
        match core with
        | None -> None
        | Some (alias, centerName, brokerName) ->
            let study = Study()
            study.Accession <- accession
            study.Alias <- alias
            study.CenterName <- centerName
            study.BrokerName <- brokerName
            study.Descriptor <- readDescriptor connection accession
            study.Identifiers <- Identifiers.read connection (identifierOwner accession)
            for attr in Attributes.read connection (attributeOwner accession) do
                study.StudyAttributes.Add(attr)
            for link in Links.read connection (linkOwner accession) do
                study.StudyLinks.Add(link)
            Some study

    /// Removes the row from `study`; cascades through every owned table.
    let delete (connection: SqliteConnection) (accession: string) : unit =
        Sql.withTransaction connection (fun _ ->
            Sql.execNonQuery
                connection
                "DELETE FROM study WHERE accession = @acc;"
                [ "@acc", box accession ]
            |> ignore)

    /// Lists every Study accession in the database, lexicographically.
    let listAccessions (connection: SqliteConnection) : string seq =
        Sql.queryAll
            connection
            "SELECT accession FROM study ORDER BY accession;"
            []
            (fun reader -> reader.GetString(0))
        :> string seq
