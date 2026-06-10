namespace BioFSharp.IO.INSDC

open BioFSharp.FileFormats.INSDC
open BioFSharp.IO.INSDC.Internal

/// Read and write INSDC Receipt records (the response document returned by the
/// ENA submission API).
module Receipt =

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

    /// Resolve the absolute XPath of a property of a parsed `receipt`. Name the property with a
    /// quotation, addressing array positions with `.[i]`:
    /// `receipt |> Receipt.xpathOf <@ fun r -> r.Submission.Accession @>`.
    let xpathOf selector (receipt: Receipt) : string =
        XPathTracking.xpathOf selector receipt

    /// As `xpathOf`, but wrapped as a W3C XPointer fragment selector (`#xpointer(...)`).
    let xpointerOf selector (receipt: Receipt) : string =
        XPathTracking.xpointerOf selector receipt
