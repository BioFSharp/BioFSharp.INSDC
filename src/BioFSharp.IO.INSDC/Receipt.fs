namespace BioFSharp.IO.INSDC

open BioFSharp.IO.INSDC.Internal

/// Read and write INSDC Receipt records (the response document returned by the
/// ENA submission API).
module Receipt =

    /// An INSDC Receipt record. Alias for `BioFSharp.FileFormats.INSDC.Receipt`.
    type Receipt = BioFSharp.FileFormats.INSDC.Receipt

    /// Read an INSDC Receipt XML record from the file at `filePath`.
    let read (filePath: string) : Receipt =
        XmlSerializer.read<Receipt> filePath

    /// Parse an INSDC Receipt XML record from an in-memory string.
    let readString (xml: string) : Receipt =
        XmlSerializer.readString<Receipt> xml

    /// Write an INSDC Receipt `receipt` to the file at `filePath` as XML.
    let write (filePath: string) (receipt: Receipt) : unit =
        XmlSerializer.write filePath receipt

    /// Serialize an INSDC Receipt `receipt` to an XML string.
    let writeString (receipt: Receipt) : string =
        XmlSerializer.writeString receipt
