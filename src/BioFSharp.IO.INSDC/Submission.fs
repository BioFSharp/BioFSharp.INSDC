namespace BioFSharp.IO.INSDC

open BioFSharp.FileFormats.INSDC
open BioFSharp.IO.INSDC.Internal

/// Read and write INSDC Submission records.
module Submission =

    /// Read an INSDC Submission XML record from the file at `filePath`.
    let read (filePath: string) : Submission =
        XmlSerializer.read<Submission> filePath

    /// Parse an INSDC Submission XML record from an in-memory string.
    let readString (xml: string) : Submission =
        XmlSerializer.readString<Submission> xml

    /// Write an INSDC Submission `submission` to the file at `filePath` as XML.
    let write (filePath: string) (submission: Submission) : unit =
        XmlSerializer.write filePath submission

    /// Serialize an INSDC Submission `submission` to an XML string.
    let writeString (submission: Submission) : string =
        XmlSerializer.writeString submission
