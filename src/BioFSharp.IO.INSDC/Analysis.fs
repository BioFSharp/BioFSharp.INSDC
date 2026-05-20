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
