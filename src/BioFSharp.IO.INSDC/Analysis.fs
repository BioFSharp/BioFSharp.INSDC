namespace BioFSharp.IO.INSDC

open BioFSharp.FileFormats.INSDC
open BioFSharp.IO.INSDC.Internal

/// Read and write INSDC Analysis records.
module Analysis =

    /// Read INSDC Analysis XML records from the file at `filePath`.
    let read (filePath: string) : seq<Analysis> =
        XmlSerializer.readOrSet<Analysis, AnalysisSet> "ANALYSIS_SET" (fun set -> set.Analysis) filePath

    /// Parse INSDC Analysis XML records from an in-memory string.
    let readString (xml: string) : seq<Analysis> =
        XmlSerializer.readStringOrSet<Analysis, AnalysisSet> "ANALYSIS_SET" (fun set -> set.Analysis) xml

    /// Write an INSDC Analysis `analysis` to the file at `filePath` as XML.
    let write (filePath: string) (analysis: Analysis) : unit =
        XmlSerializer.write filePath analysis

    /// Serialize an INSDC Analysis `analysis` to an XML string.
    let writeString (analysis: Analysis) : string =
        XmlSerializer.writeString analysis

    /// Resolve the absolute XPath of a property of a parsed `analysis`. Name the property with a
    /// quotation, addressing array positions with `.[i]`:
    /// `analysis |> Analysis.xpathOf <@ fun a -> a.Files.[0].Filename @>`.
    let xpathOf selector (analysis: Analysis) : string =
        XPathTracking.xpathOf selector analysis

    /// As `xpathOf`, but wrapped as a W3C XPointer fragment selector (`#xpointer(...)`).
    let xpointerOf selector (analysis: Analysis) : string =
        XPathTracking.xpointerOf selector analysis

    /// Every present leaf of a parsed `analysis` as an `XPathEntry` (property path, positional XPath,
    /// value) — a serializable, position-qualified DTO of the whole record for a web API.
    let xpathEntries (analysis: Analysis) : XPathEntry[] =
        XPathTracking.xpathEntries analysis
