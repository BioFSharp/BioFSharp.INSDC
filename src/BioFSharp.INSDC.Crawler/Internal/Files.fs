namespace BioFSharp.INSDC.Crawler.Internal

open System
open System.IO
open System.Text

/// Same-directory atomic file writes used for every downloaded crawler
/// artifact. A unique temporary file is flushed before one final rename, and
/// is cleaned up on cancellation or failure.
module internal Files =

    let private atomicWrite (path: string) (write: FileStream -> unit) =
        let fullPath = Path.GetFullPath(path)
        let directory = Path.GetDirectoryName(fullPath)
        Directory.CreateDirectory(directory) |> ignore

        let tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp"

        try
            let writeTemporaryFile () =
                use stream =
                    new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)

                write stream
                stream.Flush(true)

            writeTemporaryFile ()
            File.Move(tempPath, fullPath, true)
        finally
            if File.Exists(tempPath) then
                File.Delete(tempPath)

    /// Atomically writes UTF-8 text without a byte-order mark to `path`.
    let writeText (path: string) (content: string) =
        atomicWrite path (fun stream ->
            use writer = new StreamWriter(stream, UTF8Encoding(false), 4096, leaveOpen = true)
            writer.Write(content)
            writer.Flush())

    /// Atomically writes `content` to `path`.
    let writeBytes (path: string) (content: byte[]) =
        atomicWrite path (fun stream -> stream.Write(content, 0, content.Length))
