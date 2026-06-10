namespace BioFSharp.IO.INSDC

open BioFSharp.FileFormats.INSDC
open BioFSharp.IO.INSDC.Internal

/// Read and write INSDC Study records.
module Study =

    /// Read INSDC Study XML records from the file at `filePath`.
    let read (filePath: string) : seq<Study> =
        XmlSerializer.readOrSet<Study, StudySet> "STUDY_SET" (fun set -> set.Study) filePath

    /// Parse INSDC Study XML records from an in-memory string.
    let readString (xml: string) : seq<Study> =
        XmlSerializer.readStringOrSet<Study, StudySet> "STUDY_SET" (fun set -> set.Study) xml

    /// Write an INSDC Study `study` to the file at `filePath` as XML.
    let write (filePath: string) (study: Study) : unit =
        XmlSerializer.write filePath study

    /// Serialize an INSDC Study `study` to an XML string.
    let writeString (study: Study) : string =
        XmlSerializer.writeString study

    /// Resolve the position-qualified W3C XPointer fragment selector for a property of a parsed
    /// `study`. Name the property with a quotation, addressing array positions with `.[i]`:
    /// `study |> Study.xpathOf <@ fun s -> s.Descriptor.StudyTitle @>`.
    let xpathOf selector (study: Study) : string =
        XPathTracking.xpathOf selector study
