namespace BioFSharp.IO.INSDC

open BioFSharp.FileFormats.INSDC
open BioFSharp.IO.INSDC.Internal

/// Read and write INSDC BioSample records (called `Sample` in the SRA/ENA
/// XML schema; `BioSample` is the cross-archive INSDC name used throughout
/// this library).
module BioSample =

    /// Read INSDC BioSample XML records from the file at `filePath`.
    let read (filePath: string) : seq<BioSample> =
        XmlSerializer.readOrSet<BioSample, BioSampleSet> "SAMPLE_SET" (fun set -> set.Sample) filePath

    /// Parse INSDC BioSample XML records from an in-memory string.
    let readString (xml: string) : seq<BioSample> =
        XmlSerializer.readStringOrSet<BioSample, BioSampleSet> "SAMPLE_SET" (fun set -> set.Sample) xml

    /// Write an INSDC BioSample `sample` to the file at `filePath` as XML.
    let write (filePath: string) (sample: BioSample) : unit =
        XmlSerializer.write filePath sample

    /// Serialize an INSDC BioSample `sample` to an XML string.
    let writeString (sample: BioSample) : string =
        XmlSerializer.writeString sample
