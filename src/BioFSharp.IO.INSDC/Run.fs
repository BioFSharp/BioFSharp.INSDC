namespace BioFSharp.IO.INSDC

open BioFSharp.FileFormats.INSDC
open BioFSharp.IO.INSDC.Internal

/// Read and write INSDC Run records.
module Run =

    /// Read INSDC Run XML records from the file at `filePath`.
    let read (filePath: string) : seq<Run> =
        XmlSerializer.readOrSet<Run, RunSet> "RUN_SET" (fun set -> set.Run) filePath

    /// Parse INSDC Run XML records from an in-memory string.
    let readString (xml: string) : seq<Run> =
        XmlSerializer.readStringOrSet<Run, RunSet> "RUN_SET" (fun set -> set.Run) xml

    /// Write an INSDC Run `run` to the file at `filePath` as XML.
    let write (filePath: string) (run: Run) : unit =
        XmlSerializer.write filePath run

    /// Serialize an INSDC Run `run` to an XML string.
    let writeString (run: Run) : string =
        XmlSerializer.writeString run
