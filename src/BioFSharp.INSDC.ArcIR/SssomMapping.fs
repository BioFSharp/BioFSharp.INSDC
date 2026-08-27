namespace BioFSharp.INSDC.ArcIR

open System
open System.Security.Cryptography
open System.Text
open BioFSharp.ArcIR
open SSSOM

/// Neutral claims and all diagnostics produced from one SSSOM document.
type SssomClaimLoadResult =
    {
        /// Successfully projected mapping claims.
        Claims: MappingClaim list
        /// Codec and projection diagnostics in the shared ArcIR diagnostic model.
        Diagnostics: Diagnostic list
    }

/// PolyglotSSSOM-backed loading of format-neutral ArcIR mapping claims.
[<RequireQualifiedAccess>]
module SssomMapping =

    /// Absolute IRI of `skos:exactMatch` after normative SSSOM CURIE expansion.
    let exactMatchPredicate = Iri.Create "http://www.w3.org/2004/02/skos/core#exactMatch"

    let private sha256 (value: string) =
        use algorithm = SHA256.Create()

        algorithm.ComputeHash(Encoding.UTF8.GetBytes value)
        |> Array.map (fun (byte: byte) -> byte.ToString("x2"))
        |> String.concat ""

    let private diagnostic severity code message row slot =
        let context =
            [ row |> Option.map (fun value -> sprintf "row %d" value)
              slot |> Option.map (fun value -> sprintf "slot %s" value) ]
            |> List.choose id
            |> String.concat ", "

        let contextualMessage =
            if String.IsNullOrEmpty context then message else sprintf "%s (%s)" message context

        let codeId =
            Iri.Create("urn:biofsharp:arcir:diagnostic-code:sssom:" + Uri.EscapeDataString code)

        { Id = Iri.Create("urn:biofsharp:arcir:diagnostic:sssom:" + sha256 (code + "\n" + contextualMessage))
          Code = codeId
          Severity = severity
          Message = contextualMessage
          Targets = []
          Related = [] }

    let private ofCodecDiagnostic (value: SssomDiagnostic) =
        diagnostic
            (match value.Severity with
             | SSSOM.DiagnosticSeverity.Warning -> BioFSharp.ArcIR.DiagnosticSeverity.Warning
             | SSSOM.DiagnosticSeverity.Error -> BioFSharp.ArcIR.DiagnosticSeverity.Error)
            value.Code
            value.Message
            value.Row
            value.Slot

    let private tryIri curieMap row slot (value: EntityReference) =
        try
            Ok(CurieMap.expand curieMap value.Value |> Iri.Create)
        with ex ->
            Error(
                diagnostic
                    BioFSharp.ArcIR.DiagnosticSeverity.Error
                    "arcir.sssom.invalid-iri"
                    ex.Message
                    (Some row)
                    (Some slot)
            )

    let private sourceText curieMap (mappingSource: EntityReference option) (setSource: EntityReference option) =
        mappingSource
        |> Option.orElse setSource
        |> Option.bind (fun value -> CurieMap.tryExpand curieMap value.Value)

    let private projectDocument validateDocument (document: SssomDocument) =
        let diagnostics = ResizeArray<Diagnostic>()
        let claims = ResizeArray<MappingClaim>()
        let curieMap = document.Metadata.CurieMap

        if validateDocument then
            SssomCodec.Validate document
            |> Array.map ofCodecDiagnostic
            |> diagnostics.AddRange

        let hasDocumentErrors =
            diagnostics
            |> Seq.exists (fun value -> value.Severity = BioFSharp.ArcIR.DiagnosticSeverity.Error)

        if not hasDocumentErrors then
            for index, mapping in document.Mappings |> Array.indexed do
                let row = index + 1

                match mapping.RecordId, mapping.SubjectId, mapping.ObjectId with
                | None, _, _ ->
                    diagnostics.Add(
                        diagnostic
                            BioFSharp.ArcIR.DiagnosticSeverity.Error
                            "arcir.sssom.missing-record-id"
                            "A stable record_id is required before a mapping can become an ArcIR claim."
                            (Some row)
                            (Some "record_id")
                    )
                | _, None, _ ->
                    diagnostics.Add(
                        diagnostic
                            BioFSharp.ArcIR.DiagnosticSeverity.Error
                            "arcir.sssom.literal-subject"
                            "Literal-subject mappings are not term mapping claims."
                            (Some row)
                            (Some "subject_id")
                    )
                | _, _, None ->
                    diagnostics.Add(
                        diagnostic
                            BioFSharp.ArcIR.DiagnosticSeverity.Error
                            "arcir.sssom.literal-object"
                            "Literal-object mappings are not yet supported by additive term enrichment."
                            (Some row)
                            (Some "object_id")
                    )
                | Some recordId, Some subjectId, Some objectId ->
                    let values =
                        [ "record_id", tryIri curieMap row "record_id" recordId
                          "subject_id", tryIri curieMap row "subject_id" subjectId
                          "predicate_id", tryIri curieMap row "predicate_id" mapping.PredicateId
                          "object_id", tryIri curieMap row "object_id" objectId
                          "mapping_justification", tryIri curieMap row "mapping_justification" mapping.MappingJustification ]

                    let errors =
                        values
                        |> List.choose (fun (_, result) ->
                            match result with
                            | Error error -> Some error
                            | Ok _ -> None)

                    if not errors.IsEmpty then
                        diagnostics.AddRange errors
                    else
                        let resolved slot =
                            values
                            |> List.find (fun (name, _) -> name = slot)
                            |> snd
                            |> function
                                | Ok value -> value
                                | Error _ -> failwith "Resolved SSSOM value unexpectedly contained an error."

                        let subjectSource =
                            sourceText curieMap mapping.SubjectSource document.Metadata.SubjectSource

                        let objectSource =
                            sourceText curieMap mapping.ObjectSource document.Metadata.ObjectSource

                        claims.Add(
                            { Id = resolved "record_id"
                              Subject = resolved "subject_id"
                              Predicate = resolved "predicate_id"
                              Object = resolved "object_id"
                              SubjectDefinition =
                                match mapping.SubjectLabel, subjectSource with
                                | None, None -> None
                                | label, source -> Some(OntologyTerm.create label source)
                              ObjectDefinition = OntologyTerm.create mapping.ObjectLabel objectSource
                              Justification = Some(resolved "mapping_justification") }
                        )

        { Claims = List.ofSeq claims
          Diagnostics = List.ofSeq diagnostics }

    /// Projects an already decoded PolyglotSSSOM document into neutral claims.
    let fromDocument document = projectDocument true document

    /// Decodes embedded SSSOM/TSV content and projects valid rows into neutral claims.
    let loadEmbedded content =
        let decoded = SssomCodec.TryDecodeEmbedded content

        match decoded.Document with
        | None ->
            { Claims = []
              Diagnostics = decoded.Diagnostics |> Array.map ofCodecDiagnostic |> List.ofArray }
        | Some document ->
            // Successful decoding has already run PolyglotSSSOM's public document validation.
            let projected = projectDocument false document

            { projected with
                Diagnostics =
                    (decoded.Diagnostics |> Array.map ofCodecDiagnostic |> List.ofArray)
                    @ projected.Diagnostics }

    /// Selects equivalence-safe exact-match claims without choosing among duplicates.
    let exactMatches result =
        result.Claims |> List.filter (fun claim -> claim.Predicate = exactMatchPredicate)
