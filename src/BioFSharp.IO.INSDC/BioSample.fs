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

    /// Resolve the absolute XPath of a property of a parsed `sample`. Name the property with a
    /// quotation, addressing array positions with `.[i]`:
    /// `sample |> BioSample.xpathOf <@ fun s -> s.SampleName.ScientificName @>`.
    let xpathOf selector (sample: BioSample) : string =
        XPathTracking.xpathOf selector sample

    /// As `xpathOf`, but wrapped as a W3C XPointer fragment selector (`#xpointer(...)`).
    let xpointerOf selector (sample: BioSample) : string =
        XPathTracking.xpointerOf selector sample

    /// Every present leaf of a parsed `sample` as an `XPathEntry` (property path, positional XPath,
    /// value) — a serializable, position-qualified DTO of the whole record for a web API.
    let xpathEntries (sample: BioSample) : XPathEntry[] =
        XPathTracking.xpathEntries sample

    /// Decompile a parsed `sample` into structural-ontology `DecompiledTerm`s — one per present leaf,
    /// coupling each value with the OBO term describing what it is: `sample |> BioSample.decompile`.
    let decompile (sample: BioSample) : DecompiledTerm list =
        StructuralOntology.decompile sample
