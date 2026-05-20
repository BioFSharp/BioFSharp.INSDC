namespace BioFSharp.IO.INSDC

open BioFSharp.FileFormats.INSDC
open BioFSharp.IO.INSDC.Internal

/// Read and write INSDC BioSample records (called `Sample` in the SRA/ENA
/// XML schema; `BioSample` is the cross-archive INSDC name used throughout
/// this library).
module BioSample =

    /// Read an INSDC BioSample XML record from the file at `filePath`.
    let read (filePath: string) : BioSample =
        XmlSerializer.read<BioSample> filePath

    /// Parse an INSDC BioSample XML record from an in-memory string.
    let readString (xml: string) : BioSample =
        XmlSerializer.readString<BioSample> xml

    /// Write an INSDC BioSample `sample` to the file at `filePath` as XML.
    let write (filePath: string) (sample: BioSample) : unit =
        XmlSerializer.write filePath sample

    /// Serialize an INSDC BioSample `sample` to an XML string.
    let writeString (sample: BioSample) : string =
        XmlSerializer.writeString sample
