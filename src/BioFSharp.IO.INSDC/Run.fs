namespace BioFSharp.IO.INSDC

open BioFSharp.IO.INSDC.Internal

/// Read and write INSDC Run records.
module Run =

    /// An INSDC Run record. Alias for `BioFSharp.FileFormats.INSDC.Run`.
    type Run = BioFSharp.FileFormats.INSDC.Run

    /// Read an INSDC Run XML record from the file at `filePath`.
    let read (filePath: string) : Run =
        XmlSerializer.read<Run> filePath

    /// Parse an INSDC Run XML record from an in-memory string.
    let readString (xml: string) : Run =
        XmlSerializer.readString<Run> xml

    /// Write an INSDC Run `run` to the file at `filePath` as XML.
    let write (filePath: string) (run: Run) : unit =
        XmlSerializer.write filePath run

    /// Serialize an INSDC Run `run` to an XML string.
    let writeString (run: Run) : string =
        XmlSerializer.writeString run
