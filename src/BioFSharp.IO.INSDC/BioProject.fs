namespace BioFSharp.IO.INSDC

open BioFSharp.FileFormats.INSDC
open BioFSharp.IO.INSDC.Internal

/// Read and write INSDC BioProject records (called `Project` in the SRA/ENA
/// XML schema; `BioProject` is the cross-archive INSDC name used throughout
/// this library).
module BioProject =

    /// Read INSDC BioProject XML records from the file at `filePath`.
    let read (filePath: string) : seq<BioProject> =
        XmlSerializer.readOrSet<BioProject, BioProjectSet> "PROJECT_SET" (fun set -> set.Project) filePath

    /// Parse INSDC BioProject XML records from an in-memory string.
    let readString (xml: string) : seq<BioProject> =
        XmlSerializer.readStringOrSet<BioProject, BioProjectSet> "PROJECT_SET" (fun set -> set.Project) xml

    /// Write an INSDC BioProject `project` to the file at `filePath` as XML.
    let write (filePath: string) (project: BioProject) : unit =
        XmlSerializer.write filePath project

    /// Serialize an INSDC BioProject `project` to an XML string.
    let writeString (project: BioProject) : string =
        XmlSerializer.writeString project

    /// Resolve the position-qualified W3C XPointer fragment selector for a property of a parsed
    /// `project`. Name the property with a quotation, addressing array positions with `.[i]`:
    /// `project |> BioProject.xpathOf <@ fun p -> p.Name @>` -> `#xpointer(/PROJECT/NAME)`.
    let xpathOf selector (project: BioProject) : string =
        XPathTracking.xpathOf selector project
