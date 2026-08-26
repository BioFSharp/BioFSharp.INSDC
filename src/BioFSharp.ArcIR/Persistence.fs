namespace BioFSharp.ArcIR

open System.IO

/// A format-neutral persistence failure.
type PersistenceError =
    {
        /// A stable machine-readable error code.
        Code: string
        /// A human-readable explanation.
        Message: string
    }

/// Contract implemented by reversible ArcIR readers.
type IArcIRReader =
    /// Reads one graph from a stream without taking ownership of the stream.
    abstract Read: Stream -> Result<ArcIR, PersistenceError list>

/// Contract implemented by deterministic ArcIR writers.
type IArcIRWriter =
    /// Writes one graph to a stream without taking ownership of the stream.
    abstract Write: Stream * ArcIR -> Result<unit, PersistenceError list>
