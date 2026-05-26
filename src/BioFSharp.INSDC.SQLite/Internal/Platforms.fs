namespace BioFSharp.INSDC.SQLite.Internal

open System
open Microsoft.Data.Sqlite
open BioFSharp.FileFormats.INSDC

/// Identity of one `Platform` row family: the platform table and its sibling
/// `_params` table for any platform-DU-specific configuration. Both
/// `experiment_platform` / `experiment_platform_params` and
/// `run_platform` / `run_platform_params` share the same column shape, so the
/// helper is parameterized over the table pair.
type PlatformOwner = {
    /// Platform table, e.g. `"experiment_platform"`.
    Table: string
    /// Params table, e.g. `"experiment_platform_params"`.
    ParamsTable: string
    /// FK column referencing the owning entity (same name in both tables).
    AccessionColumn: string
    /// Accession of the owning entity.
    Accession: string
}

/// Persists / hydrates the F# `Platform` DU against a `(table, params_table)`
/// pair. Today every platform sub-type carries a single `InstrumentModel`
/// enum; the params table exists so future per-platform fields can land
/// without a schema migration.
module Platforms =

    let private parseEnum<'T when 'T : struct
                                and 'T :> Enum
                                and 'T : (new : unit -> 'T)>
        (text: string)
        : 'T =
        Enum.Parse(typeof<'T>, text) :?> 'T

    /// Inspects which DU case is populated on `platform` and returns its
    /// `(kind, instrument_model)` pair as the schema's CHECK constraint
    /// names them. Returns `None` when no case is set.
    let private extract (platform: Platform) : (string * string) option =
        if isNull platform then None
        elif not (isNull platform.Ls454) then Some("LS454", string platform.Ls454.InstrumentModel)
        elif not (isNull platform.Illumina) then Some("ILLUMINA", string platform.Illumina.InstrumentModel)
        elif not (isNull platform.Helicos) then Some("HELICOS", string platform.Helicos.InstrumentModel)
        elif not (isNull platform.AbiSolid) then Some("ABI_SOLID", string platform.AbiSolid.InstrumentModel)
        elif not (isNull platform.CompleteGenomics) then Some("COMPLETE_GENOMICS", string platform.CompleteGenomics.InstrumentModel)
        elif not (isNull platform.Bgiseq) then Some("BGISEQ", string platform.Bgiseq.InstrumentModel)
        elif not (isNull platform.OxfordNanopore) then Some("OXFORD_NANOPORE", string platform.OxfordNanopore.InstrumentModel)
        elif not (isNull platform.PacbioSmrt) then Some("PACBIO_SMRT", string platform.PacbioSmrt.InstrumentModel)
        elif not (isNull platform.IonTorrent) then Some("ION_TORRENT", string platform.IonTorrent.InstrumentModel)
        elif not (isNull platform.Capillary) then Some("CAPILLARY", string platform.Capillary.InstrumentModel)
        elif not (isNull platform.Dnbseq) then Some("DNBSEQ", string platform.Dnbseq.InstrumentModel)
        elif not (isNull platform.Element) then Some("ELEMENT", string platform.Element.InstrumentModel)
        elif not (isNull platform.Aviti) then Some("AVITI", string platform.Aviti.InstrumentModel)
        elif not (isNull platform.Ultima) then Some("ULTIMA", string platform.Ultima.InstrumentModel)
        elif not (isNull platform.VelaDiagnostics) then Some("VELA_DIAGNOSTICS", string platform.VelaDiagnostics.InstrumentModel)
        elif not (isNull platform.Genapsys) then Some("GENAPSYS", string platform.Genapsys.InstrumentModel)
        elif not (isNull platform.Genemind) then Some("GENEMIND", string platform.Genemind.InstrumentModel)
        elif not (isNull platform.Tapestri) then Some("TAPESTRI", string platform.Tapestri.InstrumentModel)
        else None

    /// Writes one row in `owner.Table` (kind + instrument model) and zero
    /// rows in `owner.ParamsTable` (no per-platform parameters are surfaced
    /// by the current XSD). Null `platform` is a no-op.
    let write (connection: SqliteConnection) (owner: PlatformOwner) (platform: Platform) : unit =
        match extract platform with
        | None -> ()
        | Some (kind, instrumentModel) ->
            let sql =
                sprintf
                    "INSERT INTO %s (%s, kind, instrument_model) VALUES (@acc, @kind, @model);"
                    owner.Table
                    owner.AccessionColumn
            Sql.execNonQuery
                connection
                sql
                [
                    "@acc", box owner.Accession
                    "@kind", box kind
                    "@model", box instrumentModel
                ]
            |> ignore

    /// Rehydrates a `Platform` value from `owner.Table`. Returns CLR-`null`
    /// when no row exists so the parent entity's nullable property stays unset.
    let read (connection: SqliteConnection) (owner: PlatformOwner) : Platform =
        let sql =
            sprintf
                "SELECT kind, instrument_model FROM %s WHERE %s = @acc;"
                owner.Table
                owner.AccessionColumn
        let row =
            Sql.tryQueryOne
                connection
                sql
                [ "@acc", box owner.Accession ]
                (fun reader -> reader.GetString(0), Sql.readStringOrNull reader 1)
        match row with
        | None -> null
        | Some (kind, model) ->
            let platform = Platform()
            match kind with
            | "LS454" -> platform.Ls454 <- PlatformLs454(InstrumentModel = parseEnum<Model454> model)
            | "ILLUMINA" -> platform.Illumina <- PlatformIllumina(InstrumentModel = parseEnum<IlluminaModel> model)
            | "HELICOS" -> platform.Helicos <- PlatformHelicos(InstrumentModel = parseEnum<HelicosModel> model)
            | "ABI_SOLID" -> platform.AbiSolid <- PlatformAbiSolid(InstrumentModel = parseEnum<AbiSolidModel> model)
            | "COMPLETE_GENOMICS" -> platform.CompleteGenomics <- PlatformCompleteGenomics(InstrumentModel = parseEnum<CgModel> model)
            | "BGISEQ" -> platform.Bgiseq <- PlatformBgiseq(InstrumentModel = parseEnum<BgiseqModel> model)
            | "OXFORD_NANOPORE" -> platform.OxfordNanopore <- PlatformOxfordNanopore(InstrumentModel = parseEnum<OxfordNanoporeModel> model)
            | "PACBIO_SMRT" -> platform.PacbioSmrt <- PlatformPacbioSmrt(InstrumentModel = parseEnum<PacBioModel> model)
            | "ION_TORRENT" -> platform.IonTorrent <- PlatformIonTorrent(InstrumentModel = parseEnum<IontorrentModel> model)
            | "CAPILLARY" -> platform.Capillary <- PlatformCapillary(InstrumentModel = parseEnum<CapillaryModel> model)
            | "DNBSEQ" -> platform.Dnbseq <- PlatformDnbseq(InstrumentModel = parseEnum<DnbSeqModel> model)
            | "ELEMENT" -> platform.Element <- PlatformElement(InstrumentModel = parseEnum<ElementModel> model)
            | "AVITI" -> platform.Aviti <- PlatformAviti(InstrumentModel = parseEnum<AvitiModel> model)
            | "ULTIMA" -> platform.Ultima <- PlatformUltima(InstrumentModel = parseEnum<UltimaModel> model)
            | "VELA_DIAGNOSTICS" -> platform.VelaDiagnostics <- PlatformVelaDiagnostics(InstrumentModel = parseEnum<VelaDiagnosticsModel> model)
            | "GENAPSYS" -> platform.Genapsys <- PlatformGenapsys(InstrumentModel = parseEnum<GenapsysModel> model)
            | "GENEMIND" -> platform.Genemind <- PlatformGenemind(InstrumentModel = parseEnum<GeneMindModel> model)
            | "TAPESTRI" -> platform.Tapestri <- PlatformTapestri(InstrumentModel = parseEnum<TapestriModel> model)
            | other ->
                failwithf "Unexpected platform kind '%s' in %s for %s='%s'" other owner.Table owner.AccessionColumn owner.Accession
            platform
