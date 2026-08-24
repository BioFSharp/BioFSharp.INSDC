namespace BioFSharp.INSDC.SQLite

open System
open Microsoft.Data.Sqlite
open BioFSharp.FileFormats.INSDC
open BioFSharp.INSDC.SQLite.Internal

/// SQLite persistence for `BioProject`. Each public function maps a single
/// in-memory record to the full set of rows it spans (core row + identifiers
/// + attributes + links + collaborators + related-projects).
module BioProject =

    [<Literal>]
    let private accessionColumn = "bioproject_accession"

    let private identifierOwner accession : IdentifierOwner =
        { Table = "bioproject_identifiers"; AccessionColumn = accessionColumn; Accession = accession }

    let private attributeOwner accession : AttributeOwner =
        { Table = "bioproject_attributes"; AccessionColumn = accessionColumn; Accession = accession }

    let private linkOwner accession : LinkOwner =
        { Table = "bioproject_links"; AccessionColumn = accessionColumn; Accession = accession }

    let private formatDate (date: Nullable<DateTime>) : obj =
        if date.HasValue then box (date.Value.ToString("yyyy-MM-dd")) else null

    let private parseDate (text: string) : Nullable<DateTime> =
        if isNull text then Nullable() else Nullable(DateTime.Parse(text))

    let private insertCore (connection: SqliteConnection) (project: BioProject) : unit =
        Sql.execNonQuery
            connection
            "INSERT INTO bioproject (accession, alias, center_name, broker_name, name, title, description, first_public) \
             VALUES (@acc, @alias, @cn, @bn, @name, @title, @desc, @fp);"
            [
                "@acc", box project.Accession
                "@alias", box project.Alias
                "@cn", box project.CenterName
                "@bn", box project.BrokerName
                "@name", box project.Name
                "@title", box project.Title
                "@desc", box project.Description
                "@fp", formatDate project.FirstPublic
            ]
        |> ignore

    let private insertCollaborators (connection: SqliteConnection) (project: BioProject) : unit =
        if isNull project.Collaborators then () else
        project.Collaborators
        |> Seq.iteri (fun i name ->
            Sql.execNonQuery
                connection
                "INSERT INTO bioproject_collaborators (bioproject_accession, ordinal, name) VALUES (@acc, @ordinal, @name);"
                [
                    "@acc", box project.Accession
                    "@ordinal", box i
                    "@name", box name
                ]
            |> ignore)

    let private insertRelatedProjects (connection: SqliteConnection) (project: BioProject) : unit =
        if isNull project.RelatedProjects then () else
        project.RelatedProjects
        |> Seq.iteri (fun i related ->
            let kind, target =
                if not (isNull related.ParentProject) then "PARENT", related.ParentProject.Accession
                elif not (isNull related.ChildProject) then "CHILD", related.ChildProject.Accession
                elif not (isNull related.PeerProject) then "PEER", related.PeerProject.Accession
                else
                    failwithf
                        "BioProject %s related-project at ordinal %d has none of PARENT/CHILD/PEER set"
                        project.Accession i
            Sql.execNonQuery
                connection
                "INSERT INTO bioproject_related_projects (bioproject_accession, ordinal, kind, related_accession) \
                 VALUES (@acc, @ordinal, @kind, @target);"
                [
                    "@acc", box project.Accession
                    "@ordinal", box i
                    "@kind", box kind
                    "@target", box target
                ]
            |> ignore)

    let private organismParameters (organism: Organism) =
        if isNull organism then
            [ "@hasOrganism", box 0
              "@taxon", null
              "@scientific", null
              "@common", null
              "@strain", null
              "@breed", null
              "@cultivar", null
              "@isolate", null ]
        else
            [ "@hasOrganism", box 1
              "@taxon", box organism.TaxonId
              "@scientific", box organism.ScientificName
              "@common", box organism.CommonName
              "@strain", box organism.Strain
              "@breed", box organism.Breed
              "@cultivar", box organism.Cultivar
              "@isolate", box organism.Isolate ]

    let private insertProjectCompositions (connection: SqliteConnection) (project: BioProject) =
        let insertOrganism table organism =
            Sql.execNonQuery
                connection
                (sprintf
                    "INSERT INTO %s (bioproject_accession, has_organism, taxon_id, scientific_name, common_name, strain, breed, cultivar, isolate) \
                     VALUES (@acc, @hasOrganism, @taxon, @scientific, @common, @strain, @breed, @cultivar, @isolate);"
                    table)
                (("@acc", box project.Accession) :: organismParameters organism)
            |> ignore

        if not (isNull project.SubmissionProject) then
            insertOrganism "bioproject_submission_project" project.SubmissionProject.Organism

            project.SubmissionProject.SequencingProject
            |> Seq.iteri (fun ordinal prefix ->
                Sql.execNonQuery
                    connection
                    "INSERT INTO bioproject_submission_locus_tags (bioproject_accession, ordinal, locus_tag_prefix) \
                     VALUES (@acc, @ordinal, @prefix);"
                    [ "@acc", box project.Accession; "@ordinal", box ordinal; "@prefix", box prefix ]
                |> ignore)

        if not (isNull project.UmbrellaProject) then
            insertOrganism "bioproject_umbrella_project" project.UmbrellaProject.Organism

    /// Persists `project` and every row it deconstructs into. Wraps all
    /// inserts in a single transaction so FK or constraint violations leave
    /// the database untouched.
    let insert (connection: SqliteConnection) (project: BioProject) : unit =
        Sql.withTransaction connection (fun _tx ->
            insertCore connection project
            Identifiers.write connection (identifierOwner project.Accession) project.Identifiers
            Attributes.write connection (attributeOwner project.Accession) project.ProjectAttributes
            Links.write connection (linkOwner project.Accession) project.ProjectLinks
            insertCollaborators connection project
            insertRelatedProjects connection project
            insertProjectCompositions connection project)

    /// Reconstructs a `BioProject` from its accession by joining all owner
    /// tables. Returns `None` when no core row exists.
    let tryGet (connection: SqliteConnection) (accession: string) : BioProject option =
        let core =
            Sql.tryQueryOne
                connection
                "SELECT alias, center_name, broker_name, name, title, description, first_public \
                 FROM bioproject WHERE accession = @acc;"
                [ "@acc", box accession ]
                (fun reader ->
                    let alias = Sql.readStringOrNull reader 0
                    let centerName = Sql.readStringOrNull reader 1
                    let brokerName = Sql.readStringOrNull reader 2
                    let name = Sql.readStringOrNull reader 3
                    let title = Sql.readStringOrNull reader 4
                    let description = Sql.readStringOrNull reader 5
                    let firstPublic = Sql.readStringOrNull reader 6
                    alias, centerName, brokerName, name, title, description, firstPublic)
        match core with
        | None -> None
        | Some (alias, centerName, brokerName, name, title, description, firstPublic) ->
            let project = BioProject()
            project.Accession <- accession
            project.Alias <- alias
            project.CenterName <- centerName
            project.BrokerName <- brokerName
            project.Name <- name
            project.Title <- title
            project.Description <- description
            project.FirstPublic <- parseDate firstPublic
            project.Identifiers <- Identifiers.read connection (identifierOwner accession)
            for attr in Attributes.read connection (attributeOwner accession) do
                project.ProjectAttributes.Add(attr)
            for link in Links.read connection (linkOwner accession) do
                project.ProjectLinks.Add(link)
            Sql.queryAll
                connection
                "SELECT name FROM bioproject_collaborators WHERE bioproject_accession = @acc ORDER BY ordinal;"
                [ "@acc", box accession ]
                (fun reader -> reader.GetString(0))
            |> List.iter project.Collaborators.Add
            Sql.queryAll
                connection
                "SELECT kind, related_accession FROM bioproject_related_projects \
                 WHERE bioproject_accession = @acc ORDER BY ordinal;"
                [ "@acc", box accession ]
                (fun reader ->
                    let kind = reader.GetString(0)
                    let target = reader.GetString(1)
                    let related = BioProjectRelatedProjectsRelatedProject()
                    match kind with
                    | "PARENT" ->
                        related.ParentProject <- BioProjectRelatedProjectsRelatedProjectParentProject(Accession = target)
                    | "CHILD" ->
                        related.ChildProject <- BioProjectRelatedProjectsRelatedProjectChildProject(Accession = target)
                    | "PEER" ->
                        related.PeerProject <- BioProjectRelatedProjectsRelatedProjectPeerProject(Accession = target)
                    | other ->
                        failwithf "Unexpected related-project kind '%s' for bioproject '%s'" other accession
                    related)
            |> List.iter project.RelatedProjects.Add

            let readOrganism table =
                Sql.tryQueryOne
                    connection
                    (sprintf
                        "SELECT has_organism, taxon_id, scientific_name, common_name, strain, breed, cultivar, isolate \
                         FROM %s WHERE bioproject_accession = @acc;"
                        table)
                    [ "@acc", box accession ]
                    (fun reader ->
                        let hasOrganism = reader.GetInt32(0) <> 0

                        let organism =
                            if not hasOrganism then
                                null
                            else
                                let value = Organism()
                                value.TaxonId <- if reader.IsDBNull(1) then 0 else reader.GetInt32(1)
                                value.ScientificName <- Sql.readStringOrNull reader 2
                                value.CommonName <- Sql.readStringOrNull reader 3
                                value.Strain <- Sql.readStringOrNull reader 4
                                value.Breed <- Sql.readStringOrNull reader 5
                                value.Cultivar <- Sql.readStringOrNull reader 6
                                value.Isolate <- Sql.readStringOrNull reader 7
                                value

                        organism)

            match readOrganism "bioproject_submission_project" with
            | Some organism ->
                let submission = BioProjectSubmissionProject()
                submission.Organism <- organism

                Sql.queryAll
                    connection
                    "SELECT locus_tag_prefix FROM bioproject_submission_locus_tags \
                     WHERE bioproject_accession = @acc ORDER BY ordinal;"
                    [ "@acc", box accession ]
                    (fun reader -> Sql.readStringOrNull reader 0)
                |> List.iter submission.SequencingProject.Add

                project.SubmissionProject <- submission
            | None -> ()

            match readOrganism "bioproject_umbrella_project" with
            | Some organism ->
                let umbrella = BioProjectUmbrellaProject()
                umbrella.Organism <- organism
                project.UmbrellaProject <- umbrella
            | None -> ()

            Some project

    /// Removes the row from `bioproject`; every owned table's rows are deleted
    /// via the schema's ON DELETE CASCADE.
    let delete (connection: SqliteConnection) (accession: string) : unit =
        Sql.withTransaction connection (fun _ ->
            Sql.execNonQuery
                connection
                "DELETE FROM bioproject WHERE accession = @acc;"
                [ "@acc", box accession ]
            |> ignore)

    /// Lists every BioProject accession currently stored, in lexicographic
    /// order. Suitable for crawler resume / dedup bookkeeping.
    let listAccessions (connection: SqliteConnection) : string seq =
        Sql.queryAll
            connection
            "SELECT accession FROM bioproject ORDER BY accession;"
            []
            (fun reader -> reader.GetString(0))
        :> string seq
