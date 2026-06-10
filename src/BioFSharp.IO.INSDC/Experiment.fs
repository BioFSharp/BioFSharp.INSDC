namespace BioFSharp.IO.INSDC

open BioFSharp.FileFormats.INSDC
open BioFSharp.IO.INSDC.Internal

/// Read and write INSDC Experiment records.
module Experiment =

    /// Read INSDC Experiment XML records from the file at `filePath`.
    let read (filePath: string) : seq<Experiment> =
        XmlSerializer.readOrSet<Experiment, ExperimentSet> "EXPERIMENT_SET" (fun set -> set.Experiment) filePath

    /// Parse INSDC Experiment XML records from an in-memory string.
    let readString (xml: string) : seq<Experiment> =
        XmlSerializer.readStringOrSet<Experiment, ExperimentSet> "EXPERIMENT_SET" (fun set -> set.Experiment) xml

    /// Write an INSDC Experiment `experiment` to the file at `filePath` as XML.
    let write (filePath: string) (experiment: Experiment) : unit =
        XmlSerializer.write filePath experiment

    /// Serialize an INSDC Experiment `experiment` to an XML string.
    let writeString (experiment: Experiment) : string =
        XmlSerializer.writeString experiment

    /// Resolve the position-qualified W3C XPointer fragment selector for a property of a parsed
    /// `experiment`. Name the property with a quotation, addressing array positions with `.[i]`:
    /// `experiment |> Experiment.xpathOf <@ fun e -> e.StudyRef.Accession @>`.
    let xpathOf selector (experiment: Experiment) : string =
        XPathTracking.xpathOf selector experiment
