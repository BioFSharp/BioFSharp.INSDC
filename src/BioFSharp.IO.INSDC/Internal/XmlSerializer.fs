namespace BioFSharp.IO.INSDC.Internal

open System
open System.Collections.Concurrent
open System.IO
open System.Xml.Linq
open System.Xml.Serialization

/// Internal helpers wrapping `System.Xml.Serialization.XmlSerializer` for the
/// auto-generated INSDC types. Public modules in `BioFSharp.IO.INSDC` are thin
/// wrappers over these functions, so the read/write/readString/writeString
/// shape stays uniform across every INSDC entity.
module XmlSerializer =

    let private serializerCache = ConcurrentDictionary<Type, XmlSerializer>()

    /// XmlSerializer construction is expensive; cache one instance per target type.
    let private getSerializer (t: Type) : XmlSerializer =
        serializerCache.GetOrAdd(t, fun t -> XmlSerializer(t))

    /// Deserialize an INSDC record of type `'T` from the file at `filePath`.
    let read<'T> (filePath: string) : 'T =
        use stream = File.OpenRead(filePath)
        (getSerializer typeof<'T>).Deserialize(stream) :?> 'T

    /// Deserialize an INSDC record of type `'T` from an in-memory XML string.
    let readString<'T> (xml: string) : 'T =
        use reader = new StringReader(xml)
        (getSerializer typeof<'T>).Deserialize(reader) :?> 'T

    /// Deserialize INSDC records from XML that may either use the single
    /// record root (`RUN`, `STUDY`, ...) or the ENA API's set root
    /// (`RUN_SET`, `STUDY_SET`, ...).
    let readStringOrSet<'T, 'TSet> (setRootName: string) (select: 'TSet -> seq<'T>) (xml: string) : seq<'T> =
        let rootName = XDocument.Parse(xml).Root.Name.LocalName

        if rootName = setRootName then
            xml |> readString<'TSet> |> select
        else
            xml |> readString<'T> |> Seq.singleton

    /// Deserialize INSDC records from a file that may either use the single
    /// record root (`RUN`, `STUDY`, ...) or the ENA API's set root
    /// (`RUN_SET`, `STUDY_SET`, ...).
    let readOrSet<'T, 'TSet> (setRootName: string) (select: 'TSet -> seq<'T>) (filePath: string) : seq<'T> =
        File.ReadAllText(filePath)
        |> readStringOrSet<'T, 'TSet> setRootName select

    /// Serialize an INSDC record `value` to the file at `filePath`.
    let write<'T> (filePath: string) (value: 'T) : unit =
        use stream = File.Create(filePath)
        (getSerializer typeof<'T>).Serialize(stream, value)

    /// Serialize an INSDC record `value` to an XML string.
    let writeString<'T> (value: 'T) : string =
        use writer = new StringWriter()
        (getSerializer typeof<'T>).Serialize(writer, value)
        writer.ToString()
