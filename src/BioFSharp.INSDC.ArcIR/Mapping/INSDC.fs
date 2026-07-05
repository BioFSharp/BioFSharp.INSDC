namespace BioFSharp.INSDC.ArcIR

open Arc.Build
open BioFSharp.FileFormats.INSDC

/// Facade over the per-accession converters (one module per entity, in its own folder) plus `build` to
/// assemble the fragments into one graph. Each converter is explicit and decoupled from the
/// structural-ontology decompilation: values live in `ArcObject.Annotations` (composites folded to a
/// single annotation), with sub-objects and cross-entity edges built from typed field access.
[<RequireQualifiedAccess>]
module INSDC =

    let bioProject (project: BioProject) : ConversionResult = BioProjectConversion.convert project
    let bioSample (sample: BioSample) : ConversionResult = BioSampleConversion.convert sample
    let study (study: Study) : ConversionResult = StudyConversion.convert study
    let experiment (experiment: Experiment) : ConversionResult = ExperimentConversion.convert experiment
    let run (run: Run) : ConversionResult = RunConversion.convert run
    let analysis (analysis: Analysis) : ConversionResult = AnalysisConversion.convert analysis
    let submission (submission: Submission) : ConversionResult = SubmissionConversion.convert submission
    let receipt (receipt: Receipt) : ConversionResult = ReceiptConversion.convert receipt

    /// Assemble converter fragments into one graph, wiring cross-entity references afterwards.
    let build (results: ConversionResult seq) : ArcIR = Mapping.build results
