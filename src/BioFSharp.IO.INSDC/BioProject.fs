namespace BioFSharp.IO.INSDC

open BioFSharp.FileFormats.INSDC
open BioFSharp.IO.INSDC.Internal

/// Read and write INSDC BioProject records (called `Project` in the SRA/ENA
/// XML schema; `BioProject` is the cross-archive INSDC name used throughout
/// this library).
module BioProject =

    /// Read an INSDC BioProject XML record from the file at `filePath`.
    let read (filePath: string) : BioProject =
        XmlSerializer.read<BioProject> filePath

    /// Parse an INSDC BioProject XML record from an in-memory string.
    let readString (xml: string) : BioProject =
        XmlSerializer.readString<BioProject> xml

    /// Write an INSDC BioProject `project` to the file at `filePath` as XML.
    let write (filePath: string) (project: BioProject) : unit =
        XmlSerializer.write filePath project

    /// Serialize an INSDC BioProject `project` to an XML string.
    let writeString (project: BioProject) : string =
        XmlSerializer.writeString project
