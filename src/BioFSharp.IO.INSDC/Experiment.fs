namespace BioFSharp.IO.INSDC

open BioFSharp.IO.INSDC.Internal

/// Read and write INSDC Experiment records.
module Experiment =

    /// An INSDC Experiment record. Alias for `BioFSharp.FileFormats.INSDC.Experiment`.
    type Experiment = BioFSharp.FileFormats.INSDC.Experiment

    /// Read an INSDC Experiment XML record from the file at `filePath`.
    let read (filePath: string) : Experiment =
        XmlSerializer.read<Experiment> filePath

    /// Parse an INSDC Experiment XML record from an in-memory string.
    let readString (xml: string) : Experiment =
        XmlSerializer.readString<Experiment> xml

    /// Write an INSDC Experiment `experiment` to the file at `filePath` as XML.
    let write (filePath: string) (experiment: Experiment) : unit =
        XmlSerializer.write filePath experiment

    /// Serialize an INSDC Experiment `experiment` to an XML string.
    let writeString (experiment: Experiment) : string =
        XmlSerializer.writeString experiment
