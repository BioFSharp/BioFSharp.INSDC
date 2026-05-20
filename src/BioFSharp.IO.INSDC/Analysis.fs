namespace BioFSharp.IO.INSDC

open BioFSharp.FileFormats.INSDC
open BioFSharp.IO.INSDC.Internal

/// Read and write INSDC Analysis records.
module Analysis =

    /// Read an INSDC Analysis XML record from the file at `filePath`.
    let read (filePath: string) : Analysis =
        XmlSerializer.read<Analysis> filePath

    /// Parse an INSDC Analysis XML record from an in-memory string.
    let readString (xml: string) : Analysis =
        XmlSerializer.readString<Analysis> xml

    /// Write an INSDC Analysis `analysis` to the file at `filePath` as XML.
    let write (filePath: string) (analysis: Analysis) : unit =
        XmlSerializer.write filePath analysis

    /// Serialize an INSDC Analysis `analysis` to an XML string.
    let writeString (analysis: Analysis) : string =
        XmlSerializer.writeString analysis
