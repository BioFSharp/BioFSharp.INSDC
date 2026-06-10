namespace BioFSharp.IO.INSDC

open BioFSharp.FileFormats.INSDC
open BioFSharp.IO.INSDC.Internal

/// Read and write INSDC Submission records.
module Submission =

    /// Read INSDC Submission XML records from the file at `filePath`.
    let read (filePath: string) : seq<Submission> =
        XmlSerializer.readOrSet<Submission, SubmissionSet> "SUBMISSION_SET" (fun set -> set.Submission) filePath

    /// Parse INSDC Submission XML records from an in-memory string.
    let readString (xml: string) : seq<Submission> =
        XmlSerializer.readStringOrSet<Submission, SubmissionSet> "SUBMISSION_SET" (fun set -> set.Submission) xml

    /// Write an INSDC Submission `submission` to the file at `filePath` as XML.
    let write (filePath: string) (submission: Submission) : unit =
        XmlSerializer.write filePath submission

    /// Serialize an INSDC Submission `submission` to an XML string.
    let writeString (submission: Submission) : string =
        XmlSerializer.writeString submission

    /// Resolve the absolute XPath of a property of a parsed `submission`. Name the property with a
    /// quotation, addressing array positions with `.[i]`:
    /// `submission |> Submission.xpathOf <@ fun s -> s.SubmissionLinks.[0].XrefLink.Db @>`.
    let xpathOf selector (submission: Submission) : string =
        XPathTracking.xpathOf selector submission

    /// As `xpathOf`, but wrapped as a W3C XPointer fragment selector (`#xpointer(...)`).
    let xpointerOf selector (submission: Submission) : string =
        XPathTracking.xpointerOf selector submission
