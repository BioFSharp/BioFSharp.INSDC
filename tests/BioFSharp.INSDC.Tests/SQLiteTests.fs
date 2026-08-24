namespace BioFSharp.INSDC.Tests

open System
open System.IO
open Microsoft.Data.Sqlite
open Xunit

open BioFSharp.FileFormats.INSDC
open BioFSharp.IO.INSDC
open BioFSharp.INSDC.SQLite

module private SQLiteFixture =

    let openDatabase () =
        let connection = new SqliteConnection("Data Source=:memory:")
        connection.Open()
        connection

    let initialized () =
        let connection = openDatabase ()
        Schema.init connection
        connection

    let exec (connection: SqliteConnection) sql =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteNonQuery() |> ignore

    let scalarInt (connection: SqliteConnection) sql =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        Convert.ToInt32(command.ExecuteScalar())

    let versionOneSql () =
        Path.GetFullPath(
            Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "src", "BioFSharp.INSDC.SQLite", "schema", "insdc_schema.sql")
        )
        |> File.ReadAllText

type SQLiteSchemaTests() =

    [<Fact>]
    member _.``new database reaches the current schema with FK enforcement`` () =
        use connection = SQLiteFixture.initialized ()

        let tables =
            BioFSharp.INSDC.SQLite.Internal.Sql.queryAll
                connection
                "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;"
                []
                (fun reader -> reader.GetString 0)
            |> Set.ofList

        let required =
            set
                [ "bioproject"
                  "study"
                  "biosample"
                  "experiment"
                  "run"
                  "accession_relations"
                  "insdc_schema_history" ]

        Assert.True(Set.isSubset required tables, sprintf "Missing tables: %A" (Set.difference required tables))
        Assert.Equal(Schema.currentVersion, Schema.version connection)
        Assert.Equal(1, SQLiteFixture.scalarInt connection "PRAGMA foreign_keys;")
        Assert.Equal(2, SQLiteFixture.scalarInt connection "SELECT COUNT(*) FROM insdc_schema_history;")

    [<Fact>]
    member _.``Schema init is idempotent`` () =
        use connection = SQLiteFixture.initialized ()
        Schema.init connection
        Assert.Equal(Schema.currentVersion, Schema.version connection)

    [<Fact>]
    member _.``foreign key mode is explicit and reversible`` () =
        use connection = SQLiteFixture.initialized ()

        Schema.setForeignKeyMode connection ForeignKeyMode.AllowCrawlerSoftReferences
        Assert.Equal(0, SQLiteFixture.scalarInt connection "PRAGMA foreign_keys;")

        Schema.setForeignKeyMode connection ForeignKeyMode.Enforce
        Assert.Equal(1, SQLiteFixture.scalarInt connection "PRAGMA foreign_keys;")

    [<Fact>]
    member _.``version one upgrades through every committed migration`` () =
        use connection = SQLiteFixture.openDatabase ()
        SQLiteFixture.exec connection (SQLiteFixture.versionOneSql ())
        SQLiteFixture.exec connection "PRAGMA user_version = 1;"

        Schema.init connection

        Assert.Equal(2, Schema.version connection)
        Assert.Equal(2, SQLiteFixture.scalarInt connection "SELECT COUNT(*) FROM insdc_schema_history;")

    [<Fact>]
    member _.``failed migration rolls back schema changes and version`` () =
        use connection = SQLiteFixture.openDatabase ()
        SQLiteFixture.exec connection (SQLiteFixture.versionOneSql ())
        SQLiteFixture.exec connection "CREATE TABLE bioproject_submission_project (collision INTEGER);"
        SQLiteFixture.exec connection "PRAGMA user_version = 1;"

        Assert.Throws<SqliteException>(fun () -> Schema.init connection) |> ignore

        Assert.Equal(1, Schema.version connection)
        Assert.Equal(
            0,
            SQLiteFixture.scalarInt
                connection
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='insdc_schema_history';"
        )
        Assert.Equal(
            0,
            SQLiteFixture.scalarInt
                connection
                "SELECT COUNT(*) FROM pragma_table_info('experiment_spot_descriptor') WHERE name='base_coord';"
        )

    [<Fact>]
    member _.``pre-versioning database is recognized as version one and upgraded`` () =
        use connection = SQLiteFixture.openDatabase ()
        SQLiteFixture.exec connection (SQLiteFixture.versionOneSql ())
        Assert.Equal(0, Schema.version connection)

        Schema.init connection

        Assert.Equal(Schema.currentVersion, Schema.version connection)

    [<Fact>]
    member _.``unknown nonempty schema is rejected`` () =
        use connection = SQLiteFixture.openDatabase ()
        SQLiteFixture.exec connection "CREATE TABLE unrelated (id INTEGER PRIMARY KEY);"

        let error = Assert.Throws<Exception>(fun () -> Schema.init connection)
        Assert.Contains("unversioned, non-empty", error.Message)

    [<Fact>]
    member _.``future schema version is rejected`` () =
        use connection = SQLiteFixture.openDatabase ()
        SQLiteFixture.exec connection "PRAGMA user_version = 999;"

        let error = Assert.Throws<Exception>(fun () -> Schema.init connection)
        Assert.Contains("newer", error.Message)

type SQLiteIntegrityTests() =

    [<Fact>]
    member _.``foreign key enforcement rejects an experiment without its study`` () =
        use connection = SQLiteFixture.initialized ()
        let experiment = Experiment.read (TestFiles.fixture "DRX066772.xml") |> Seq.exactlyOne

        Assert.Throws<SqliteException>(fun () ->
            BioFSharp.INSDC.SQLite.Experiment.insert connection "MISSING-STUDY" experiment)
        |> ignore

        Assert.Empty(BioFSharp.INSDC.SQLite.Experiment.listAccessions connection)

    [<Fact>]
    member _.``public insert rolls back all owner rows when a detail write fails`` () =
        use connection = SQLiteFixture.initialized ()
        let project = BioProject.read (TestFiles.fixture "PRJDB5192.xml") |> Seq.exactlyOne

        SQLiteFixture.exec
            connection
            "CREATE TRIGGER reject_project_attributes BEFORE INSERT ON bioproject_attributes BEGIN SELECT RAISE(ABORT, 'forced detail failure'); END;"

        Assert.Throws<SqliteException>(fun () -> BioFSharp.INSDC.SQLite.BioProject.insert connection project)
        |> ignore

        Assert.Empty(BioFSharp.INSDC.SQLite.BioProject.listAccessions connection)
        Assert.Equal(0, SQLiteFixture.scalarInt connection "SELECT COUNT(*) FROM bioproject_identifiers;")

    [<Fact>]
    member _.``identifier kinds labels and namespaces round trip`` () =
        use connection = SQLiteFixture.initialized ()
        let project = BioProject.read (TestFiles.fixture "PRJDB5192.xml") |> Seq.exactlyOne
        let identifiers = Identifier()
        identifiers.PrimaryId <- Name(Value = "PRIMARY", Label = "primary-label")
        identifiers.SecondaryId.Add(Name(Value = "SECONDARY", Label = "secondary-label"))
        identifiers.ExternalId.Add(QualifiedName(Value = "EXTERNAL", Label = "external-label", Namespace = "ext-ns"))
        identifiers.SubmitterId <- QualifiedName(Value = "SUBMITTER", Label = "submitter-label", Namespace = "submitter-ns")
        identifiers.Uuid.Add(Name(Value = "UUID", Label = "uuid-label"))
        project.Identifiers <- identifiers

        BioFSharp.INSDC.SQLite.BioProject.insert connection project
        let restored = BioFSharp.INSDC.SQLite.BioProject.tryGet connection project.Accession |> Option.get

        ObjectGraph.equal identifiers restored.Identifiers

type SQLiteRoundTripTests() =

    [<Fact>]
    member _.``BioProject fixture round trips exactly without the crawler`` () =
        use connection = SQLiteFixture.initialized ()
        let project = BioProject.read (TestFiles.fixture "PRJDB5192.xml") |> Seq.exactlyOne

        BioFSharp.INSDC.SQLite.BioProject.insert connection project

        ObjectGraph.equal
            project
            (BioFSharp.INSDC.SQLite.BioProject.tryGet connection project.Accession |> Option.get)

    [<Fact>]
    member _.``Study fixture round trips exactly without the crawler`` () =
        use connection = SQLiteFixture.initialized ()
        let study = Study.read (TestFiles.fixture "DRP003416.xml") |> Seq.exactlyOne

        BioFSharp.INSDC.SQLite.Study.insert connection null study

        ObjectGraph.equal
            study
            (BioFSharp.INSDC.SQLite.Study.tryGet connection study.Accession |> Option.get)

    [<Fact>]
    member _.``BioSample fixture round trips exactly without the crawler`` () =
        use connection = SQLiteFixture.initialized ()
        let sample = BioSample.read (TestFiles.fixture "SAMD00064197.xml") |> Seq.exactlyOne

        BioFSharp.INSDC.SQLite.BioSample.insert connection sample

        ObjectGraph.equal
            sample
            (BioFSharp.INSDC.SQLite.BioSample.tryGet connection sample.Accession |> Option.get)

    [<Fact>]
    member _.``Experiment fixture round trips exactly without the crawler`` () =
        use connection = SQLiteFixture.initialized ()
        let study = Study.read (TestFiles.fixture "DRP003416.xml") |> Seq.exactlyOne
        let experiment = Experiment.read (TestFiles.fixture "DRX066772.xml") |> Seq.exactlyOne

        BioFSharp.INSDC.SQLite.Study.insert connection null study

        // The generated records legitimately use a second SRA accession for a
        // BioSample soft reference; exercise that documented store mode here.
        Schema.setForeignKeyMode connection ForeignKeyMode.AllowCrawlerSoftReferences
        BioFSharp.INSDC.SQLite.Experiment.insert connection study.Accession experiment

        ObjectGraph.equal
            experiment
            (BioFSharp.INSDC.SQLite.Experiment.tryGet connection experiment.Accession |> Option.get)

    [<Fact>]
    member _.``Run fixture round trips exactly without the crawler`` () =
        use connection = SQLiteFixture.initialized ()
        let study = Study.read (TestFiles.fixture "DRP003416.xml") |> Seq.exactlyOne
        let experiment = Experiment.read (TestFiles.fixture "DRX066772.xml") |> Seq.exactlyOne
        let run = Run.read (TestFiles.fixture "DRR072834.xml") |> Seq.exactlyOne

        BioFSharp.INSDC.SQLite.Study.insert connection null study
        Schema.setForeignKeyMode connection ForeignKeyMode.AllowCrawlerSoftReferences
        BioFSharp.INSDC.SQLite.Experiment.insert connection study.Accession experiment
        BioFSharp.INSDC.SQLite.Run.insert connection experiment.Accession run

        ObjectGraph.equal
            run
            (BioFSharp.INSDC.SQLite.Run.tryGet connection run.Accession |> Option.get)
