namespace BioFSharp.INSDC.ArcIR

open BioFSharp.ArcIR

/// Maps a count-matrix file into ArcIR fragments: a Resource node for the file, and one fragment per
/// data column. Each column node is addressed by the RFC 7111 CSV fragment selector `<fileId>#col=<n>`
/// (1-based position, per plans/arcir-ingest.md and plans/xml-fragment-selectors.md), links back to its
/// file via `hasColumn`, and receives a `producesData` edge from the run whose accession heads it — an
/// edge that dangles until that run node is merged into the graph. See plans/arcir-ingest.md.
[<RequireQualifiedAccess>]
module CountData =

    let private fileId (file: ResourceFile) = "count:" + file.Name

    /// Convert one count file into a `ConversionResult`: a `countMatrix` Resource node plus a `countColumn`
    /// fragment (with `hasColumn` and `producesData` edges) for each run-accession column in its header.
    let convert (countFile: CountFile) : ConversionResult =
        let file = countFile.File
        let fid = fileId file

        let fileNode =
            GraphBuilder.object' fid ArcObjectKind.Resource [ Vocabulary.DType.countMatrix; Vocabulary.DType.data ] (ResourceFile.properties file) []

        let columnFragments =
            countFile.Columns
            |> List.map (fun col ->
                let selector = sprintf "#col=%d" col.Index
                let colId = fid + selector

                let props =
                    [ Vocabulary.Property.ofName "Column", ArcValue.Integer(int64 col.Index)
                      Vocabulary.Property.ofName "RunAccession", ArcValue.String col.RunAccession
                      Vocabulary.Property.ofName "FragmentSelector", ArcValue.String selector ]

                let colNode = GraphBuilder.object' colId ArcObjectKind.Resource [ Vocabulary.DType.countColumn ] props []
                let fileToColumn = GraphBuilder.relation fid Vocabulary.Rel.hasColumn colId [] []
                let runToColumn = GraphBuilder.relation col.RunAccession Vocabulary.Rel.producesData colId [] []
                colNode, [ fileToColumn; runToColumn ])

        {
            Objects = fileNode :: (columnFragments |> List.map fst)
            Relations = columnFragments |> List.collect snd
            Pending = []
        }

    /// Convert several count files, one `ConversionResult` each.
    let convertMany (files: CountFile seq) : ConversionResult list =
        files |> Seq.map convert |> List.ofSeq
