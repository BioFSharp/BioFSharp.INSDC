namespace BioFSharp.INSDC.ArcIR

open System
open BioFSharp.ArcIR

/// Maps a paper (its file + extracted authors) into ArcIR fragments: a Resource node for the file, a
/// person Agent per author (deduped, so authors merge with INSDC contacts sharing an email), and pending
/// `references` edges to the dataset accession(s) the paper describes. Cross-entity references dangle
/// until the referenced record is merged in, like every other converter. See plans/arcir-ingest.md.
[<RequireQualifiedAccess>]
module Paper =

    [<Literal>]
    let private source = "paper"

    let private present (value: string option) =
        match value with
        | Some s when not (String.IsNullOrWhiteSpace s) -> Some s
        | _ -> None

    /// The paper Resource node id: `doi:<doi>` when a DOI is present (stable across file copies), else
    /// `paper:<name>`.
    let internal paperId (meta: PaperMetadata) (file: ResourceFile) : string =
        match present meta.Doi with
        | Some doi -> "doi:" + doi.Trim().ToLowerInvariant()
        | None -> "paper:" + file.Name

    /// An author's dedup id: `agent:<email>`, else `agent:<orcid>`, else `agent:<name>`; `None` if all blank.
    let internal authorId (a: PaperAuthor) : string option =
        [ a.Email; a.Orcid; a.Name ]
        |> List.tryPick present
        |> Option.map (fun key -> "agent:" + key.Trim().ToLowerInvariant())

    let internal authorFragment (paperId: string) (a: PaperAuthor) : (ArcObject * ArcRelation) option =
        authorId a
        |> Option.map (fun authorNodeId ->
            let props =
                [ ArcValueConversion.stringProp "Name" (Option.toObj a.Name)
                  ArcValueConversion.stringProp "Email" (Option.toObj a.Email)
                  ArcValueConversion.stringProp "Affiliation" (Option.toObj a.Affiliation)
                  ArcValueConversion.stringProp "Orcid" (Option.toObj a.Orcid) ]
                |> List.choose id
            SubObjects.person paperId authorNodeId props)

    /// Convert a paper into a `ConversionResult`: a `publication` Resource node for the file, its authors
    /// as person Agents (`hasContact`), and a pending `references` edge to each related dataset accession.
    let convert (meta: PaperMetadata) (file: ResourceFile) (relatedAccessions: string list) : ConversionResult =
        let nodeId = paperId meta file

        let annotations =
            [ Annotations.stringField source "Title" (Option.toObj meta.Title)
              Annotations.stringField source "DOI" (Option.toObj meta.Doi)
              Annotations.stringField source "Journal" (Option.toObj meta.Journal) ]
            |> List.choose id

        let node =
            GraphBuilder.object' nodeId ArcObjectKind.Resource [ Vocabulary.DType.publication ] (ResourceFile.properties file) annotations

        let authors = meta.Authors |> List.choose (authorFragment nodeId)

        let pending =
            relatedAccessions
            |> List.choose (Convert.pendingAccession nodeId Vocabulary.Rel.references)

        Convert.result node authors pending
