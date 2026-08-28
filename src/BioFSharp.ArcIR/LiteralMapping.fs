namespace BioFSharp.ArcIR

/// One selected string-literal occurrence and the registered term to add as its semantic companion.
type LiteralTermMapping =
    {
        /// Typed location of the exact source occurrence.
        Source: ArcJsonLocation
        /// Exact source literal expected at the selected location.
        Literal: string
        /// Existing graph-level term to add as a semantic companion.
        Target: Iri
    }

/// Whether a selected-literal mapping added or reused its deterministic companion.
type LiteralTermMappingStatus =
    /// The semantic companion was added.
    | Added
    /// The identical deterministic companion was already present.
    | AlreadyPresent

/// Occurrence-level accounting for one selected-literal mapping application.
type LiteralTermMappingApplication =
    {
        /// Existing string-literal input occurrence.
        Input: ArcJsonLocation
        /// Deterministic term-valued companion occurrence.
        Output: ArcJsonLocation
        /// Exact source literal retained by the graph.
        Literal: string
        /// Registered target term added or reused by the operation.
        Target: Iri
        /// Whether the companion was added or reused.
        Status: LiteralTermMappingStatus
    }

/// A typed failure that prevents a selected-literal mapping from returning a graph.
type LiteralTermMappingFailure =
    /// The selected location is not one of the supported property or annotation value occurrences.
    | UnsupportedLiteralLocation of location: ArcJsonLocation
    /// The selected object, relation, assertion, or annotation does not exist.
    | LiteralLocationNotFound of location: ArcJsonLocation
    /// The selected value is not a scalar string literal.
    | ExpectedStringLiteral of location: ArcJsonLocation
    /// The selected value differs from the exact literal carried by the command.
    | SourceLiteralMismatch of location: ArcJsonLocation * expected: string * actual: string
    /// The requested target is absent from the graph-level term registry.
    | LiteralTargetTermNotRegistered of target: Iri
    /// The deterministic companion identity already carries incompatible content.
    | LiteralCompanionConflict of location: ArcJsonLocation * companionId: Iri
    /// The input or candidate output graph has structural or referential validation failures.
    | InvalidLiteralMappingGraph of issues: ValidationIssue list

/// The immutable graph and occurrence accounting produced by a selected-literal mapping.
type LiteralTermMappingResult =
    {
        /// Source-preserving graph containing the semantic companion.
        Graph: ArcIR
        /// Input/output locations and deterministic application status.
        Application: LiteralTermMappingApplication
    }

/// Format-neutral transformation from one selected string literal to an additive term companion.
[<RequireQualifiedAccess>]
module LiteralMapping =

    let private failure value = Error [ value ]

    let private validateLiteral (command: LiteralTermMapping) (actual: string) =
        if actual = command.Literal then
            Ok()
        else
            failure (SourceLiteralMismatch(command.Source, command.Literal, actual))

    let private addCompanion
        (command: LiteralTermMapping)
        (owner: Iri)
        (inputId: Iri)
        (role: string)
        (candidate: Iri -> 'value)
        (values: Map<Iri, 'value>)
        (outputLocation: Iri -> ArcJsonLocation)
        =
        let outputId = SemanticCompanion.id owner inputId role command.Target
        let expected = candidate outputId

        match Map.tryFind outputId values with
        | None ->
            Ok(
                Map.add outputId expected values,
                { Input = command.Source
                  Output = outputLocation outputId
                  Literal = command.Literal
                  Target = command.Target
                  Status = LiteralTermMappingStatus.Added }
            )
        | Some existing when existing = expected ->
            Ok(
                values,
                { Input = command.Source
                  Output = outputLocation outputId
                  Literal = command.Literal
                  Target = command.Target
                  Status = LiteralTermMappingStatus.AlreadyPresent }
            )
        | Some _ -> failure (LiteralCompanionConflict(command.Source, outputId))

    let private mapProperty
        (command: LiteralTermMapping)
        (owner: Iri)
        (outputLocation: Iri -> ArcJsonLocation)
        (assertionId: Iri)
        (properties: Map<Iri, ArcProperty>)
        =
        match Map.tryFind assertionId properties with
        | None -> failure (LiteralLocationNotFound command.Source)
        | Some property ->
            match property.Value with
            | ArcValue.String actual ->
                validateLiteral command actual
                |> Result.bind (fun () ->
                    addCompanion
                        command
                        owner
                        property.Id
                        "literal-property-value"
                        (fun outputId ->
                            { property with
                                Id = outputId
                                Value = ArcValue.Iri command.Target })
                        properties
                        outputLocation)
            | _ -> failure (ExpectedStringLiteral command.Source)

    let private mappedAnnotationValue (target: Iri) (value: AnnotationValue) =
        match value with
        | AnnotationValue.Literal(ArcValue.String _) -> AnnotationValue.Term target
        | AnnotationValue.LiteralWithUnit(ArcValue.String _, unit) -> AnnotationValue.TermWithUnit(target, unit)
        | _ -> invalidArg (nameof value) "A mapped annotation value must contain a scalar string literal."

    let private annotationLiteral (value: AnnotationValue) =
        match value with
        | AnnotationValue.Literal(ArcValue.String actual)
        | AnnotationValue.LiteralWithUnit(ArcValue.String actual, _) -> Some actual
        | _ -> None

    let private mapAnnotation
        (command: LiteralTermMapping)
        (owner: Iri)
        (outputLocation: Iri -> ArcJsonLocation)
        (annotationId: Iri)
        (annotations: Map<Iri, ArcAnnotation>)
        =
        match Map.tryFind annotationId annotations with
        | None -> failure (LiteralLocationNotFound command.Source)
        | Some annotation ->
            match annotationLiteral annotation.Value with
            | None -> failure (ExpectedStringLiteral command.Source)
            | Some actual ->
                validateLiteral command actual
                |> Result.bind (fun () ->
                    addCompanion
                        command
                        owner
                        annotation.Id
                        "literal-annotation-value"
                        (fun outputId ->
                            { annotation with
                                Id = outputId
                                Value = mappedAnnotationValue command.Target annotation.Value })
                        annotations
                        outputLocation)

    let private finish (graph: ArcIR) (application: LiteralTermMappingApplication) =
        match Validation.validate graph with
        | [] -> Ok { Graph = graph; Application = application }
        | issues -> failure (InvalidLiteralMappingGraph issues)

    /// Applies one selected string-literal mapping without replacing the source occurrence.
    let apply (command: LiteralTermMapping) (graph: ArcIR) =
        match Validation.validate graph with
        | issues when not (List.isEmpty issues) -> failure (InvalidLiteralMappingGraph issues)
        | _ when not (Map.containsKey command.Target graph.Terms) ->
            failure (LiteralTargetTermNotRegistered command.Target)
        | _ ->
            match command.Source with
            | ArcJsonLocation.PropertyValue(objectId, assertionId) ->
                match Map.tryFind objectId graph.Objects with
                | None -> failure (LiteralLocationNotFound command.Source)
                | Some object' ->
                    mapProperty
                        command
                        objectId
                        (fun outputId -> ArcJsonLocation.PropertyValue(objectId, outputId))
                        assertionId
                        object'.Properties
                    |> Result.bind (fun (properties, application) ->
                        let updated = { object' with Properties = properties }
                        let result = { graph with Objects = Map.add objectId updated graph.Objects }
                        finish result application)
            | ArcJsonLocation.RelationPropertyValue(relationId, assertionId) ->
                match Map.tryFind relationId graph.Relations with
                | None -> failure (LiteralLocationNotFound command.Source)
                | Some relation ->
                    mapProperty
                        command
                        relationId
                        (fun outputId -> ArcJsonLocation.RelationPropertyValue(relationId, outputId))
                        assertionId
                        relation.Properties
                    |> Result.bind (fun (properties, application) ->
                        let updated = { relation with Properties = properties }
                        let result = { graph with Relations = Map.add relationId updated graph.Relations }
                        finish result application)
            | ArcJsonLocation.ObjectAnnotationValue(objectId, annotationId) ->
                match Map.tryFind objectId graph.Objects with
                | None -> failure (LiteralLocationNotFound command.Source)
                | Some object' ->
                    mapAnnotation
                        command
                        objectId
                        (fun outputId -> ArcJsonLocation.ObjectAnnotationValue(objectId, outputId))
                        annotationId
                        object'.Annotations
                    |> Result.bind (fun (annotations, application) ->
                        let updated = { object' with Annotations = annotations }
                        let result = { graph with Objects = Map.add objectId updated graph.Objects }
                        finish result application)
            | ArcJsonLocation.RelationAnnotationValue(relationId, annotationId) ->
                match Map.tryFind relationId graph.Relations with
                | None -> failure (LiteralLocationNotFound command.Source)
                | Some relation ->
                    mapAnnotation
                        command
                        relationId
                        (fun outputId -> ArcJsonLocation.RelationAnnotationValue(relationId, outputId))
                        annotationId
                        relation.Annotations
                    |> Result.bind (fun (annotations, application) ->
                        let updated = { relation with Annotations = annotations }
                        let result = { graph with Relations = Map.add relationId updated graph.Relations }
                        finish result application)
            | ArcJsonLocation.PropertyAnnotationValue(objectId, assertionId, annotationId) ->
                match Map.tryFind objectId graph.Objects with
                | None -> failure (LiteralLocationNotFound command.Source)
                | Some object' ->
                    match Map.tryFind assertionId object'.Properties with
                    | None -> failure (LiteralLocationNotFound command.Source)
                    | Some property ->
                        mapAnnotation
                            command
                            property.Id
                            (fun outputId ->
                                ArcJsonLocation.PropertyAnnotationValue(objectId, assertionId, outputId))
                            annotationId
                            property.Annotations
                        |> Result.bind (fun (annotations, application) ->
                            let updatedProperty = { property with Annotations = annotations }
                            let updatedObject =
                                { object' with
                                    Properties = Map.add assertionId updatedProperty object'.Properties }
                            let result = { graph with Objects = Map.add objectId updatedObject graph.Objects }
                            finish result application)
            | ArcJsonLocation.RelationPropertyAnnotationValue(relationId, assertionId, annotationId) ->
                match Map.tryFind relationId graph.Relations with
                | None -> failure (LiteralLocationNotFound command.Source)
                | Some relation ->
                    match Map.tryFind assertionId relation.Properties with
                    | None -> failure (LiteralLocationNotFound command.Source)
                    | Some property ->
                        mapAnnotation
                            command
                            property.Id
                            (fun outputId ->
                                ArcJsonLocation.RelationPropertyAnnotationValue(
                                    relationId,
                                    assertionId,
                                    outputId
                                ))
                            annotationId
                            property.Annotations
                        |> Result.bind (fun (annotations, application) ->
                            let updatedProperty = { property with Annotations = annotations }
                            let updatedRelation =
                                { relation with
                                    Properties = Map.add assertionId updatedProperty relation.Properties }
                            let result = { graph with Relations = Map.add relationId updatedRelation graph.Relations }
                            finish result application)
            | unsupported -> failure (UnsupportedLiteralLocation unsupported)
