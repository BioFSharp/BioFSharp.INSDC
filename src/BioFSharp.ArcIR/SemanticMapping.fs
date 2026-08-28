namespace BioFSharp.ArcIR

/// A format-neutral, entity-to-entity semantic mapping claim.
type MappingClaim =
    {
        /// Stable identity of the mapping claim, such as a SSSOM record_id.
        Id: Iri
        /// Source ontology term matched in the graph.
        Subject: Iri
        /// Semantic relationship asserted between source and target terms.
        Predicate: Iri
        /// Target ontology term added as a companion assertion.
        Object: Iri
        /// Optional descriptive definition of the source term supplied by the mapping artifact.
        SubjectDefinition: OntologyTerm option
        /// Descriptive definition used when adding the target term to the graph registry.
        ObjectDefinition: OntologyTerm
        /// Optional mapping-justification term.
        Justification: Iri option
    }

/// Whether an additive mapping operation created its output or found it already present.
type MappingApplicationStatus =
    /// The companion assertion was added by this operation.
    | Added
    /// The identical deterministic companion assertion was already present.
    | AlreadyPresent

/// One source occurrence and the companion occurrence generated for a mapping claim.
type MappingApplication =
    {
        /// Identity of the mapping claim that was applied.
        ClaimId: Iri
        /// Existing source-faithful graph occurrence.
        Input: ArcJsonLocation
        /// Additive mapped companion occurrence.
        Output: ArcJsonLocation
        /// Whether this invocation added or reused the companion occurrence.
        Status: MappingApplicationStatus
    }

/// A conflict that prevents an additive mapping operation from returning a graph.
type MappingConflict =
    /// The target term already has an incompatible graph-level definition.
    | MappingTermConflict of id: Iri * existing: OntologyTerm * incoming: OntologyTerm
    /// A deterministic mapped assertion identity already carries incompatible content.
    | MappedAssertionConflict of owner: Iri * assertion: Iri

/// The graph and occurrence-level accounting produced by additive mapping.
type MappingResult =
    {
        /// Source-preserving graph containing mapped companion assertions.
        Graph: ArcIR
        /// Every source occurrence matched by the selected claims.
        Applications: MappingApplication list
    }

/// Format-neutral, additive semantic enrichment over ArcIR graphs.
[<RequireQualifiedAccess>]
module SemanticMapping =

    let private statusAndMap (owner: Iri) (assertion: Iri) candidate values (conflicts: ResizeArray<MappingConflict>) =
        match Map.tryFind assertion values with
        | None ->
            MappingApplicationStatus.Added,
            Map.add assertion candidate values
        | Some existing when existing = candidate ->
            MappingApplicationStatus.AlreadyPresent,
            values
        | Some _ ->
            conflicts.Add(MappedAssertionConflict(owner, assertion))
            MappingApplicationStatus.AlreadyPresent, values

    let private application (claim: MappingClaim) input output status =
        { ClaimId = claim.Id
          Input = input
          Output = output
          Status = status }

    let rec private mapArcValueTerm subject target value =
        match value with
        | ArcValue.Iri term when term = subject -> true, ArcValue.Iri target
        | ArcValue.List values ->
            let mapped = values |> List.map (mapArcValueTerm subject target)

            if mapped |> List.exists fst then
                true, ArcValue.List(mapped |> List.map snd)
            else
                false, value
        | _ -> false, value

    let private mapAnnotationValueTerm subject target value =
        match value with
        | AnnotationValue.Term term when term = subject ->
            Some("annotation-value", AnnotationValue.Term target)
        | AnnotationValue.TermWithUnit(valueTerm, unitTerm) ->
            match valueTerm = subject, unitTerm = subject with
            | true, true -> Some("annotation-value-unit", AnnotationValue.TermWithUnit(target, target))
            | true, false -> Some("annotation-value", AnnotationValue.TermWithUnit(target, unitTerm))
            | false, true -> Some("annotation-unit", AnnotationValue.TermWithUnit(valueTerm, target))
            | false, false -> None
        | AnnotationValue.LiteralWithUnit(value, unitTerm) when unitTerm = subject ->
            Some("annotation-unit", AnnotationValue.LiteralWithUnit(value, target))
        | _ -> None

    let private enrichAnnotations
        (claim: MappingClaim)
        (owner: Iri)
        inputLocation
        outputLocation
        valueInputLocation
        valueOutputLocation
        (annotations: Map<Iri, ArcAnnotation>)
        (conflicts: ResizeArray<MappingConflict>)
        (applications: ResizeArray<MappingApplication>)
        =
        let mutable result = annotations

        for annotation in annotations.Values do
            if not (SemanticCompanion.isId annotation.Id) then
                if annotation.Property = claim.Subject then
                    let outputId = SemanticCompanion.id owner annotation.Id "annotation-property" claim.Object

                    let mapped =
                        { annotation with
                            Id = outputId
                            Property = claim.Object }

                    let status, updated = statusAndMap owner outputId mapped result conflicts
                    result <- updated
                    applications.Add(application claim (inputLocation annotation.Id) (outputLocation outputId) status)

                match mapAnnotationValueTerm claim.Subject claim.Object annotation.Value with
                | None -> ()
                | Some(role, mappedValue) ->
                    let outputId = SemanticCompanion.id owner annotation.Id role claim.Object

                    let mapped =
                        { annotation with
                            Id = outputId
                            Value = mappedValue }

                    let status, updated = statusAndMap owner outputId mapped result conflicts
                    result <- updated
                    applications.Add(application claim (valueInputLocation annotation.Id) (valueOutputLocation outputId) status)

        result

    let private enrichProperties
        (claim: MappingClaim)
        (owner: Iri)
        inputLocation
        outputLocation
        valueInputLocation
        valueOutputLocation
        annotationInput
        annotationOutput
        annotationValueInput
        annotationValueOutput
        (properties: Map<Iri, ArcProperty>)
        (conflicts: ResizeArray<MappingConflict>)
        (applications: ResizeArray<MappingApplication>)
        =
        let mutable result = properties

        for property in properties.Values do
            if not (SemanticCompanion.isId property.Id) then
                let annotations =
                    enrichAnnotations
                        claim
                        property.Id
                        (annotationInput property.Id)
                        (annotationOutput property.Id)
                        (annotationValueInput property.Id)
                        (annotationValueOutput property.Id)
                        property.Annotations
                        conflicts
                        applications

                if annotations <> property.Annotations then
                    result <- Map.add property.Id { property with Annotations = annotations } result

                if property.Predicate = claim.Subject then
                    let outputId = SemanticCompanion.id owner property.Id "property-predicate" claim.Object

                    let mapped =
                        { property with
                            Id = outputId
                            Predicate = claim.Object }

                    let status, updated = statusAndMap owner outputId mapped result conflicts
                    result <- updated
                    applications.Add(application claim (inputLocation property.Id) (outputLocation outputId) status)

                let valueWasMapped, mappedValue = mapArcValueTerm claim.Subject claim.Object property.Value

                if valueWasMapped then
                    let outputId = SemanticCompanion.id owner property.Id "property-value" claim.Object

                    let mapped =
                        { property with
                            Id = outputId
                            Value = mappedValue }

                    let status, updated = statusAndMap owner outputId mapped result conflicts
                    result <- updated
                    applications.Add(application claim (valueInputLocation property.Id) (valueOutputLocation outputId) status)

        result

    let private applyOne (claim: MappingClaim) (graph: ArcIR) =
        if claim.Subject = claim.Object then
            Ok { Graph = graph; Applications = [] }
        else
            let conflicts = ResizeArray<MappingConflict>()
            let applications = ResizeArray<MappingApplication>()
            let mutable objects = graph.Objects
            let mutable relations = graph.Relations

            for object' in graph.Objects.Values do
                let mutable types = object'.Types

                for assertion in object'.Types.Values do
                    if not (SemanticCompanion.isId assertion.Id) && assertion.Term = claim.Subject then
                        let outputId = SemanticCompanion.id object'.Id assertion.Id "type" claim.Object
                        let mapped = { Id = outputId; Term = claim.Object }
                        let status, updated = statusAndMap object'.Id outputId mapped types conflicts
                        types <- updated

                        applications.Add(
                            application
                                claim
                                (ArcJsonLocation.TypeAssertion(object'.Id, assertion.Id))
                                (ArcJsonLocation.TypeAssertion(object'.Id, outputId))
                                status
                        )

                let properties =
                    enrichProperties
                        claim
                        object'.Id
                        (fun assertionId -> ArcJsonLocation.Property(object'.Id, assertionId))
                        (fun assertionId -> ArcJsonLocation.Property(object'.Id, assertionId))
                        (fun assertionId -> ArcJsonLocation.PropertyValue(object'.Id, assertionId))
                        (fun assertionId -> ArcJsonLocation.PropertyValue(object'.Id, assertionId))
                        (fun assertionId annotationId -> ArcJsonLocation.PropertyAnnotation(object'.Id, assertionId, annotationId))
                        (fun assertionId annotationId -> ArcJsonLocation.PropertyAnnotation(object'.Id, assertionId, annotationId))
                        (fun assertionId annotationId -> ArcJsonLocation.PropertyAnnotationValue(object'.Id, assertionId, annotationId))
                        (fun assertionId annotationId -> ArcJsonLocation.PropertyAnnotationValue(object'.Id, assertionId, annotationId))
                        object'.Properties
                        conflicts
                        applications

                let annotations =
                    enrichAnnotations
                        claim
                        object'.Id
                        (fun annotationId -> ArcJsonLocation.ObjectAnnotation(object'.Id, annotationId))
                        (fun annotationId -> ArcJsonLocation.ObjectAnnotation(object'.Id, annotationId))
                        (fun annotationId -> ArcJsonLocation.ObjectAnnotationValue(object'.Id, annotationId))
                        (fun annotationId -> ArcJsonLocation.ObjectAnnotationValue(object'.Id, annotationId))
                        object'.Annotations
                        conflicts
                        applications

                if types <> object'.Types || properties <> object'.Properties || annotations <> object'.Annotations then
                    objects <-
                        Map.add
                            object'.Id
                            { object' with
                                Types = types
                                Properties = properties
                                Annotations = annotations }
                            objects

            for relation in graph.Relations.Values do
                if not (SemanticCompanion.isId relation.Id) then
                    let properties =
                        enrichProperties
                            claim
                            relation.Id
                            (fun assertionId -> ArcJsonLocation.RelationProperty(relation.Id, assertionId))
                            (fun assertionId -> ArcJsonLocation.RelationProperty(relation.Id, assertionId))
                            (fun assertionId -> ArcJsonLocation.RelationPropertyValue(relation.Id, assertionId))
                            (fun assertionId -> ArcJsonLocation.RelationPropertyValue(relation.Id, assertionId))
                            (fun assertionId annotationId -> ArcJsonLocation.RelationPropertyAnnotation(relation.Id, assertionId, annotationId))
                            (fun assertionId annotationId -> ArcJsonLocation.RelationPropertyAnnotation(relation.Id, assertionId, annotationId))
                            (fun assertionId annotationId -> ArcJsonLocation.RelationPropertyAnnotationValue(relation.Id, assertionId, annotationId))
                            (fun assertionId annotationId -> ArcJsonLocation.RelationPropertyAnnotationValue(relation.Id, assertionId, annotationId))
                            relation.Properties
                            conflicts
                            applications

                    let annotations =
                        enrichAnnotations
                            claim
                            relation.Id
                            (fun annotationId -> ArcJsonLocation.RelationAnnotation(relation.Id, annotationId))
                            (fun annotationId -> ArcJsonLocation.RelationAnnotation(relation.Id, annotationId))
                            (fun annotationId -> ArcJsonLocation.RelationAnnotationValue(relation.Id, annotationId))
                            (fun annotationId -> ArcJsonLocation.RelationAnnotationValue(relation.Id, annotationId))
                            relation.Annotations
                            conflicts
                            applications

                    if properties <> relation.Properties || annotations <> relation.Annotations then
                        relations <-
                            Map.add
                                relation.Id
                                { relation with
                                    Properties = properties
                                    Annotations = annotations }
                                relations

                    if relation.Predicate = claim.Subject then
                        let outputId = SemanticCompanion.id relation.Id relation.Id "relation-predicate" claim.Object

                        let mapped =
                            { relation with
                                Id = outputId
                                Predicate = claim.Object }

                        let status, updated = statusAndMap relation.Id outputId mapped relations conflicts
                        relations <- updated
                        applications.Add(application claim (ArcJsonLocation.Relation relation.Id) (ArcJsonLocation.Relation outputId) status)

            if applications.Count = 0 then
                Ok { Graph = graph; Applications = [] }
            else
                match Map.tryFind claim.Object graph.Terms with
                | Some existing when existing <> claim.ObjectDefinition ->
                    conflicts.Add(MappingTermConflict(claim.Object, existing, claim.ObjectDefinition))
                | _ -> ()

                if conflicts.Count > 0 then
                    Error(List.ofSeq conflicts)
                else
                    let terms =
                        graph.Terms
                        |> Map.add claim.Object claim.ObjectDefinition
                        |> fun values ->
                            match claim.SubjectDefinition with
                            | Some definition when not (Map.containsKey claim.Subject values) ->
                                Map.add claim.Subject definition values
                            | _ -> values

                    Ok
                        { Graph =
                            { graph with
                                Terms = terms
                                Objects = objects
                                Relations = relations }
                          Applications = List.ofSeq applications }

    /// Applies one selected mapping claim without replacing any source assertion.
    let applyClaim claim graph = applyOne claim graph

    /// Applies selected claims in order and returns no partially enriched graph if a conflict occurs.
    let applyClaims claims graph =
        ((Ok { Graph = graph; Applications = [] }), claims)
        ||> Seq.fold (fun state claim ->
            state
            |> Result.bind (fun accumulated ->
                applyOne claim accumulated.Graph
                |> Result.map (fun current ->
                    { Graph = current.Graph
                      Applications = accumulated.Applications @ current.Applications })))
