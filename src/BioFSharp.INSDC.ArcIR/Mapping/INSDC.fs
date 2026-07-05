namespace BioFSharp.INSDC.ArcIR

open Arc.Build
open BioFSharp.FileFormats.INSDC

/// Per-entity converters from the typed INSDC object model into ArcIR fragments, plus `build` to assemble
/// them into a graph. This is the "structure" half of the mapping — typed values, sub-objects (via
/// `SubObjects`) and explicit edges built from typed field access, referencing the controlled `Vocabulary`
/// — while `Ontology` supplies the semantic annotation layer on top.
[<RequireQualifiedAccess>]
module INSDC =

    type private InsdcObject = BioFSharp.FileFormats.INSDC.Object
    type private InsdcAttribute = BioFSharp.FileFormats.INSDC.Attribute

    let private iri (name: string) = Iri.Create name
    let private strProp = ArcValueConversion.stringProp

    /// The ArcId string for an entity: its accession, falling back to its alias.
    let private entityId (o: #InsdcObject) : string =
        if not (System.String.IsNullOrWhiteSpace o.Accession) then o.Accession
        elif not (System.String.IsNullOrWhiteSpace o.Alias) then o.Alias
        else invalidArg "o" "INSDC record has neither accession nor alias; cannot assign an ArcId."

    /// Base `Object` fields (accession/alias/center/broker) as candidate properties (absent ones are `None`).
    let private baseProperties (o: #InsdcObject) : (Iri * ArcValue) option list =
        [ strProp "Accession" o.Accession
          strProp "Alias" o.Alias
          strProp "CenterName" o.CenterName
          strProp "BrokerName" o.BrokerName ]

    /// A cross-entity reference (`RefObject`) as a `PendingRelation` from `subject`, resolved later.
    let private pendingRef (subject: string) (predicate: Iri) (refObj: #RefObject) : PendingRelation =
        {
            Subject = ArcId.Create subject
            Predicate = predicate
            TargetAccession = Option.ofObj refObj.Accession
            TargetRefname = Option.ofObj refObj.Refname
            TargetRefcenter = Option.ofObj refObj.Refcenter
        }

    /// A pending edge to a record identified by a bare accession string (for non-`RefObject` links).
    let private pendingAccession (subject: string) (predicate: Iri) (accession: string) : PendingRelation option =
        if System.String.IsNullOrWhiteSpace accession then
            None
        else
            Some
                {
                    Subject = ArcId.Create subject
                    Predicate = predicate
                    TargetAccession = Some accession
                    TargetRefname = None
                    TargetRefcenter = None
                }

    /// Center/broker institution strings as Agent(organization) sub-objects (deduped by name).
    let private institutionAgents (nodeId: string) (o: #InsdcObject) : (ArcObject * ArcRelation) list =
        [ o.CenterName; o.BrokerName ] |> List.choose (SubObjects.organization nodeId)

    /// Assemble an entity node + its sub-object fragments + cross-entity pending refs into a result.
    let private result (node: ArcObject) (subs: (ArcObject * ArcRelation) list) (pending: PendingRelation list) : ConversionResult =
        let subNodes, subEdges = List.unzip subs
        { Objects = node :: subNodes; Relations = subEdges; Pending = pending }

    /// BioProject -> a `Collection` node (also typed as an ISA Investigation). Delegates to the explicit,
    /// decompilation-decoupled converter — the per-accession restructure exemplar.
    let bioProject (project: BioProject) : ConversionResult = BioProjectConversion.convert project

    /// BioSample -> an `Observable` (input material) plus its organism/taxon sub-object.
    let bioSample (sample: BioSample) : ConversionResult =
        let nodeId = entityId sample
        let scalars = [ strProp "Title" sample.Title; strProp "Description" sample.Description ]
        let props = baseProperties sample @ scalars |> List.choose id
        let node =
            ArcObject.create nodeId ArcObjectKind.Observable [ Vocabulary.DType.bioSample; Vocabulary.DType.sample ] props (Ontology.annotationsOf sample @ Annotations.attributeAnnotations sample.SampleAttributes)
        let organism = if isNull sample.SampleName then [] else [ SubObjects.organism nodeId sample.SampleName ]
        result node (organism @ institutionAgents nodeId sample) []

    /// Experiment -> an `Activity` (assay): instrument + library-protocol sub-objects, edges to study/sample.
    let experiment (experiment: Experiment) : ConversionResult =
        let nodeId = entityId experiment
        let designDescription =
            match experiment.Design with
            | null -> None
            | design -> strProp "DesignDescription" design.DesignDescription
        let props = baseProperties experiment @ [ strProp "Title" experiment.Title; designDescription ] |> List.choose id
        let node =
            ArcObject.create nodeId ArcObjectKind.Activity [ Vocabulary.DType.experiment; Vocabulary.DType.assay ] props (Ontology.annotationsOf experiment @ Annotations.attributeAnnotations experiment.ExperimentAttributes)
        let instrument = if isNull experiment.Platform then [] else SubObjects.instrument nodeId experiment.Platform |> Option.toList
        let protocol =
            match experiment.Design with
            | null -> []
            | design when isNull design.LibraryDescriptor -> []
            | design -> [ SubObjects.protocol nodeId design.LibraryDescriptor ]
        let samplePending =
            match experiment.Design with
            | null -> []
            | design ->
                match design.SampleDescriptor with
                | null -> []
                | sd ->
                    let pooled =
                        if isNull sd.Pool then
                            []
                        else
                            (if isNull sd.Pool.DefaultMember then [] else [ pendingRef nodeId Vocabulary.Rel.hasSample sd.Pool.DefaultMember ])
                            @ (sd.Pool.Member |> Seq.map (pendingRef nodeId Vocabulary.Rel.hasSample) |> List.ofSeq)
                    pendingRef nodeId Vocabulary.Rel.hasSample sd :: pooled
        let studyPending =
            match experiment.StudyRef with
            | null -> []
            | sr -> [ pendingRef nodeId Vocabulary.Rel.hasStudy sr ]
        result node (instrument @ protocol @ institutionAgents nodeId experiment) (studyPending @ samplePending)

    /// Study -> a `Collection` node carrying its descriptor (title/abstract/type/project-id).
    let study (study: Study) : ConversionResult =
        let nodeId = entityId study
        let descriptorProps =
            match study.Descriptor with
            | null -> []
            | d ->
                let studyType =
                    match d.Study with
                    | null -> None
                    | st when not (System.String.IsNullOrWhiteSpace st.NewStudyType) -> Some(iri "StudyType", ArcValue.String st.NewStudyType)
                    | st -> Some(iri "StudyType", ArcValueConversion.ofEnum st.ExistingStudyType)
                [ strProp "StudyTitle" d.StudyTitle
                  strProp "StudyAbstract" d.StudyAbstract
                  strProp "StudyDescription" d.StudyDescription
                  (if d.ProjectId.HasValue then Some(iri "ProjectId", ArcValueConversion.ofInt64 d.ProjectId.Value) else None)
                  studyType ]
        let props = baseProperties study @ descriptorProps |> List.choose id
        let node =
            ArcObject.create nodeId ArcObjectKind.Collection [ Vocabulary.DType.study ] props (Ontology.annotationsOf study @ Annotations.attributeAnnotations study.StudyAttributes)
        result node (institutionAgents nodeId study) []

    /// Run -> an `Activity`: instrument + data-file sub-objects, an edge to its experiment.
    let run (run: Run) : ConversionResult =
        let nodeId = entityId run
        let runDate = ArcValueConversion.ofNullableDateTime run.RunDate |> Option.map (fun v -> iri "RunDate", v)
        let props = baseProperties run @ [ strProp "Title" run.Title; strProp "RunCenter" run.RunCenter; runDate ] |> List.choose id
        let node =
            ArcObject.create nodeId ArcObjectKind.Activity [ Vocabulary.DType.run ] props (Ontology.annotationsOf run @ Annotations.attributeAnnotations run.RunAttributes)
        let instrument = if isNull run.Platform then [] else SubObjects.instrument nodeId run.Platform |> Option.toList
        let dataFiles =
            if isNull run.DataBlock then []
            else run.DataBlock.Files |> Seq.map (SubObjects.runFile nodeId) |> List.ofSeq
        let experimentPending = if isNull run.ExperimentRef then [] else [ pendingRef nodeId Vocabulary.Rel.hasExperiment run.ExperimentRef ]
        result node (instrument @ dataFiles @ institutionAgents nodeId run) experimentPending

    /// Analysis -> an `Activity` (the reference hub): edges to study/sample/experiment/run/analysis, data files.
    let analysis (analysis: Analysis) : ConversionResult =
        let nodeId = entityId analysis
        let analysisType =
            match analysis.AnalysisProperty with
            | null -> None
            | at ->
                at.GetType().GetProperties()
                |> Array.tryPick (fun p -> if isNull (p.GetValue at) then None else Some p.Name)
                |> Option.map (fun name -> iri "AnalysisType", ArcValue.String name)
        let analysisDate = ArcValueConversion.ofNullableDateTime analysis.AnalysisDate |> Option.map (fun v -> iri "AnalysisDate", v)
        let scalars = [ strProp "Title" analysis.Title; strProp "Description" analysis.Description; strProp "AnalysisCenter" analysis.AnalysisCenter; analysisDate; analysisType ]
        let props = baseProperties analysis @ scalars |> List.choose id
        let node =
            ArcObject.create nodeId ArcObjectKind.Activity [ Vocabulary.DType.analysis ] props (Ontology.annotationsOf analysis @ Annotations.attributeAnnotations analysis.AnalysisAttributes)
        let dataFiles = analysis.Files |> Seq.map (SubObjects.analysisFile nodeId) |> List.ofSeq
        let centerAgent = SubObjects.organization nodeId analysis.AnalysisCenter |> Option.toList
        let pending =
            [ (if isNull analysis.StudyRef then [] else [ pendingRef nodeId Vocabulary.Rel.hasStudy analysis.StudyRef ])
              (analysis.SampleRef |> Seq.map (pendingRef nodeId Vocabulary.Rel.hasSample) |> List.ofSeq)
              (analysis.ExperimentRef |> Seq.map (pendingRef nodeId Vocabulary.Rel.hasExperiment) |> List.ofSeq)
              (analysis.RunRef |> Seq.map (pendingRef nodeId Vocabulary.Rel.hasRun) |> List.ofSeq)
              (analysis.AnalysisRef |> Seq.map (pendingRef nodeId Vocabulary.Rel.hasAnalysis) |> List.ofSeq) ]
            |> List.concat
        result node (dataFiles @ centerAgent @ institutionAgents nodeId analysis) pending

    /// Submission -> a `Collection` node with its contacts as Agent sub-objects.
    let submission (submission: Submission) : ConversionResult =
        let nodeId = entityId submission
        let submissionDate = ArcValueConversion.ofNullableDateTime submission.SubmissionDate |> Option.map (fun v -> iri "SubmissionDate", v)
        let scalars = [ strProp "Title" submission.Title; strProp "SubmissionComment" submission.SubmissionComment; strProp "LabName" submission.LabName; submissionDate ]
        let props = baseProperties submission @ scalars |> List.choose id
        let node =
            ArcObject.create nodeId ArcObjectKind.Collection [ Vocabulary.DType.submission ] props (Ontology.annotationsOf submission @ Annotations.attributeAnnotations submission.SubmissionAttributes)
        let contacts = submission.Contacts |> Seq.choose (SubObjects.submissionContact nodeId) |> List.ofSeq
        let lab = SubObjects.organization nodeId submission.LabName |> Option.toList
        result node (contacts @ lab @ institutionAgents nodeId submission) []

    /// Receipt -> an `Activity` acknowledging the submitted objects. Receipt is not an INSDC `Object`, so
    /// its id/properties are assembled bespoke.
    let receipt (receipt: Receipt) : ConversionResult =
        let nodeId =
            let bySubmission = if isNull receipt.Submission then null else receipt.Submission.Accession
            [ receipt.SubmissionFile; bySubmission ]
            |> List.tryFind (System.String.IsNullOrWhiteSpace >> not)
            |> Option.defaultValue "receipt"
        let props =
            [ Some(iri "Success", ArcValueConversion.ofBool receipt.Success)
              Some(iri "ReceiptDate", ArcValueConversion.ofDateTime receipt.ReceiptDate)
              strProp "SubmissionFile" receipt.SubmissionFile ]
            |> List.choose id
        let node =
            ArcObject.create nodeId ArcObjectKind.Activity [ Vocabulary.DType.receipt ] props (Ontology.annotationsOf receipt)
        let ack (bucket: seq<Id>) = bucket |> Seq.choose (fun i -> pendingAccession nodeId Vocabulary.Rel.acknowledges i.Accession) |> List.ofSeq
        let submissionAck =
            if isNull receipt.Submission then [] else pendingAccession nodeId Vocabulary.Rel.acknowledges receipt.Submission.Accession |> Option.toList
        let pending =
            [ receipt.Analysis; receipt.Experiment; receipt.Run; receipt.Sample; receipt.Study; receipt.Project; receipt.Dataset; receipt.Policy; receipt.Dac; receipt.Checklist; receipt.Samplegroup ]
            |> List.collect ack
            |> List.append submissionAck
        result node [] pending

    /// Assemble converter fragments into one graph, wiring cross-entity references afterwards.
    let build (results: ConversionResult seq) : ArcIR = Mapping.build results
