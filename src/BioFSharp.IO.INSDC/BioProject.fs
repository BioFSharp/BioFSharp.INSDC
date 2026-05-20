namespace BioFSharp.IO.INSDC

open BioFSharp.IO.INSDC.Internal

/// Read and write INSDC BioProject records. Note that the SRA / ENA schema
/// names this entity simply `Project`; `BioProject` is the cross-archive
/// INSDC name used in this module to match colloquial usage.
module BioProject =

    /// An INSDC BioProject record. Alias for `BioFSharp.FileFormats.INSDC.Project`.
    type Project = BioFSharp.FileFormats.INSDC.Project

    /// Read an INSDC BioProject XML record from the file at `filePath`.
    let read (filePath: string) : Project =
        XmlSerializer.read<Project> filePath

    /// Parse an INSDC BioProject XML record from an in-memory string.
    let readString (xml: string) : Project =
        XmlSerializer.readString<Project> xml

    /// Write an INSDC BioProject `project` to the file at `filePath` as XML.
    let write (filePath: string) (project: Project) : unit =
        XmlSerializer.write filePath project

    /// Serialize an INSDC BioProject `project` to an XML string.
    let writeString (project: Project) : string =
        XmlSerializer.writeString project
