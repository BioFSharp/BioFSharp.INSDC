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
