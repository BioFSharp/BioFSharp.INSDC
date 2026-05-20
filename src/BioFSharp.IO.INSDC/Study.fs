namespace BioFSharp.IO.INSDC

open BioFSharp.IO.INSDC.Internal

/// Read and write INSDC Study records.
module Study =

    /// An INSDC Study record. Alias for `BioFSharp.FileFormats.INSDC.Study`.
    type Study = BioFSharp.FileFormats.INSDC.Study

    /// Read an INSDC Study XML record from the file at `filePath`.
    let read (filePath: string) : Study =
        XmlSerializer.read<Study> filePath

    /// Parse an INSDC Study XML record from an in-memory string.
    let readString (xml: string) : Study =
        XmlSerializer.readString<Study> xml

    /// Write an INSDC Study `study` to the file at `filePath` as XML.
    let write (filePath: string) (study: Study) : unit =
        XmlSerializer.write filePath study

    /// Serialize an INSDC Study `study` to an XML string.
    let writeString (study: Study) : string =
        XmlSerializer.writeString study
