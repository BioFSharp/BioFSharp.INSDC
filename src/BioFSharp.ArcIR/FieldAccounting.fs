namespace BioFSharp.ArcIR

/// How one present source-field occurrence was handled by F1.
[<RequireQualifiedAccess>]
type FieldAccountingOutcome =
    /// The source occurrence produced one or more locations in the resulting ArcIR graph.
    | Emitted of outputs: ArcJsonLocation list
    /// A reviewed rule intentionally omitted the source occurrence.
    | Ignored of reason: string
    /// The source occurrence is not supported and has a corresponding diagnostic.
    | Unsupported of diagnosticId: Iri
    /// Processing the source occurrence failed and has a corresponding diagnostic.
    | Failed of diagnosticId: Iri

/// Accounting for exactly one artifact-qualified source-field occurrence.
type FieldAccountingEntry =
    {
        /// Stable identity of the extraction or handling rule.
        RuleId: Iri
        /// Exact source occurrence consumed by the rule.
        Input: FragmentRef
        /// Result of handling the source occurrence.
        Outcome: FieldAccountingOutcome
    }

/// In-memory F1 field accounting and its sibling diagnostic report.
type FieldAccountingReport =
    {
        /// One entry for every present source-field occurrence in scope.
        Entries: FieldAccountingEntry list
        /// Diagnostics referenced by unsupported or failed outcomes.
        Diagnostics: DiagnosticReport
    }

/// Artifact-qualified input/output binding derived from an emitted accounting entry.
type FieldProvenanceBinding =
    {
        /// Stable identity of the rule that produced the outputs.
        RuleId: Iri
        /// Artifact-qualified source occurrence.
        Input: FragmentRef
        /// Artifact-qualified ArcIR occurrences in the persisted output state.
        Outputs: FragmentRef list
    }

/// Validation and output-qualification helpers for F1 field accounting.
[<RequireQualifiedAccess>]
module FieldAccounting =

    let private diagnosticId outcome =
        match outcome with
        | FieldAccountingOutcome.Unsupported id
        | FieldAccountingOutcome.Failed id -> Some id
        | _ -> None

    /// Creates a deterministic report and validates occurrence and diagnostic references.
    let create (diagnostics: DiagnosticReport) (entries: FieldAccountingEntry seq) =
        let entries =
            entries
            |> Seq.sortBy (fun entry ->
                entry.Input.Artifact.Path,
                entry.Input.Artifact.Sha256,
                entry.Input.Selector.ConformsTo.Value,
                entry.Input.Selector.Value)
            |> List.ofSeq

        let duplicateInputs =
            entries
            |> Seq.countBy (fun entry -> entry.Input)
            |> Seq.filter (fun (_, count) -> count > 1)
            |> Seq.map fst
            |> List.ofSeq

        if not (List.isEmpty duplicateInputs) then
            invalidArg (nameof entries) "Each source-field occurrence must have exactly one accounting entry."

        for entry in entries do
            match entry.Outcome with
            | FieldAccountingOutcome.Emitted [] ->
                invalidArg (nameof entries) "An emitted field must identify at least one ArcIR output location."
            | FieldAccountingOutcome.Ignored reason when System.String.IsNullOrWhiteSpace reason ->
                invalidArg (nameof entries) "An ignored field must carry a non-empty reason."
            | outcome ->
                match diagnosticId outcome with
                | Some id when not (diagnostics.Diagnostics.ContainsKey id) ->
                    invalidArg (nameof diagnostics) (sprintf "Accounting references missing diagnostic: %s" id.Value)
                | _ -> ()

        { Entries = entries; Diagnostics = diagnostics }

    /// Qualifies every emitted ArcIR output location with the immutable output artifact revision.
    let qualifyEmitted outputArtifact report =
        report.Entries
        |> List.choose (fun entry ->
            match entry.Outcome with
            | FieldAccountingOutcome.Emitted locations ->
                Some
                    { RuleId = entry.RuleId
                      Input = entry.Input
                      Outputs = locations |> List.map (ArcIRJson.fragmentRef outputArtifact) }
            | _ -> None)
