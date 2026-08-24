namespace BioFSharp.INSDC.ArcIR

open Arc.Build
open BioFSharp.FileFormats.INSDC

/// Facade over the per-accession converters (one module per entity, in its own folder) plus `build` to
/// assemble the fragments into one graph. Each converter is explicit and decoupled from the
/// structural-ontology decompilation: values live in `ArcObject.Annotations` (composites folded to a
/// single annotation), with sub-objects and cross-entity edges built from typed field access.
[<RequireQualifiedAccess>]
module INSDC =

    /// Converts one BioProject record to a graph fragment.
    let bioProject (project: BioProject) : ConversionResult = BioProjectConversion.convert project
    /// Converts one BioSample record to a graph fragment.
    let bioSample (sample: BioSample) : ConversionResult = BioSampleConversion.convert sample
    /// Converts one Study record to a graph fragment.
    let study (study: Study) : ConversionResult = StudyConversion.convert study
    /// Converts one Experiment record to a graph fragment.
    let experiment (experiment: Experiment) : ConversionResult = ExperimentConversion.convert experiment
    /// Converts one Run record to a graph fragment.
    let run (run: Run) : ConversionResult = RunConversion.convert run
    /// Converts one Analysis record to a graph fragment.
    let analysis (analysis: Analysis) : ConversionResult = AnalysisConversion.convert analysis
    /// Converts one Submission record to a graph fragment.
    let submission (submission: Submission) : ConversionResult = SubmissionConversion.convert submission
    /// Converts one Receipt record to a graph fragment.
    let receipt (receipt: Receipt) : ConversionResult = ReceiptConversion.convert receipt

    /// Assemble converter fragments into one graph, wiring cross-entity references afterwards.
    let build (results: ConversionResult seq) : ArcIR = Mapping.build results
