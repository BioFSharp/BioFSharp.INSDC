namespace BioFSharp.IO.INSDC

open BioFSharp.IO.INSDC.Internal

/// Read and write INSDC Sample records (a.k.a. BioSample at the cross-archive level).
module Sample =

    /// An INSDC Sample record. Alias for `BioFSharp.FileFormats.INSDC.Sample`.
    type Sample = BioFSharp.FileFormats.INSDC.Sample

    /// Read an INSDC Sample XML record from the file at `filePath`.
    let read (filePath: string) : Sample =
        XmlSerializer.read<Sample> filePath

    /// Parse an INSDC Sample XML record from an in-memory string.
    let readString (xml: string) : Sample =
        XmlSerializer.readString<Sample> xml

    /// Write an INSDC Sample `sample` to the file at `filePath` as XML.
    let write (filePath: string) (sample: Sample) : unit =
        XmlSerializer.write filePath sample

    /// Serialize an INSDC Sample `sample` to an XML string.
    let writeString (sample: Sample) : string =
        XmlSerializer.writeString sample
