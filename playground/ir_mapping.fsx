#load "ArcCore.fs"
#load "ArcIR.fs"
#load "ArcObject.fs"

#r "nuget: BioFSharp, 2.0.0-preview.3"
#r "nuget: OBO.NET, 0.6.0"
#r "nuget: System.ComponentModel.Annotations, 5.0.0"

// Locally-built project assemblies. Paths are relative to this script's directory (playground/).
#r "../src/BioFSharp.FileFormats.INSDC/bin/Debug/netstandard2.0/BioFSharp.FileFormats.INSDC.dll"
#r "../src/BioFSharp.IO.INSDC/bin/Debug/netstandard2.0/BioFSharp.IO.INSDC.dll"

open Arc.Build
open System.IO
open BioFSharp.IO.INSDC

// __SOURCE_DIRECTORY__ is playground/, so the fixtures live one level up under tests/fixtures.
let bioproject_fixture =
    Path.Combine(__SOURCE_DIRECTORY__, "..", "tests", "fixtures", "PRJDB5192.xml")

let biosample_fixture =
    Path.Combine(__SOURCE_DIRECTORY__, "..", "tests", "fixtures", "SAMD00064197.xml")

let experiment_fixture =
    Path.Combine(__SOURCE_DIRECTORY__, "..", "tests", "fixtures", "DRX066772.xml")

let project = BioProject.read bioproject_fixture |> Seq.head

let sample = BioSample.read biosample_fixture |> Seq.head

let experiment = Experiment.read experiment_fixture |> Seq.head

open ArcIR

module OntologyHelpers =

    let toArcTerm (src_name:string option) (t:OBO.NET.OboTerm) =
        ArcAnnotation.term t.Id (Some t.Name) src_name

    let toArcTermValue (property_src_name:string option) (property:OBO.NET.OboTerm) (value_src_name:string option) (value:OBO.NET.OboTerm) =
        ArcAnnotation.termValue (toArcTerm property_src_name property) (toArcTerm value_src_name value)

    let toArcliteralWithUnit value (unit_src_name:string option) (unit:OBO.NET.OboTerm) = 
        ArcAnnotation.literalWithUnit (toArcTerm unit_src_name unit) value


let decompiled_project = BioProject.decompile project

open System
open System.Text

let projectAttributeToProperty (attr: BioFSharp.FileFormats.INSDC.Attribute) =
    // pure fallback, we can do more with annotations
    Iri.Create(attr.Tag), ArcValue.String attr.Value

let project_ir =
    let ir = ArcIR.Empty
    let dtypes = 
        [
            "BioProject", Iri.Create "BioProject"
            "Investigation", Iri.Create "Investigation"
        ] 
        |> Map.ofList
    let container = 
        ArcObject.create
            project.Accession
            Collection
            [
                dtypes["BioProject"]
                dtypes["Investigation"]
            ]
            [
                yield!
                    project.ProjectAttributes
                    |> List.ofSeq
                    |> List.map projectAttributeToProperty
                Iri.Create "Accession", ArcValue.String project.Accession
                Iri.Create "Alias", ArcValue.String project.Alias
                Iri.Create "BrokerName", ArcValue.String project.BrokerName
                Iri.Create "CenterName", ArcValue.String project.CenterName
                Iri.Create "CollaboratorsSpecified", ArcValue.Boolean project.CollaboratorsSpecified
                Iri.Create "Name", ArcValue.String project.Name
                Iri.Create "Title", ArcValue.String project.Title
            ]
            []
    container