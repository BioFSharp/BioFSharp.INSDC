namespace BioFSharp.ArcIR

open System
open System.Security.Cryptography

/// An immutable artifact occurrence identified by its path and exact byte digest.
type ArtifactRevision =
    private
    | ArtifactRevision of path: string * sha256: string * commit: string option

    /// The artifact path as designated by its containing ARC.
    member this.Path =
        let (ArtifactRevision(path, _, _)) = this
        path

    /// The lowercase hexadecimal SHA-256 digest of the artifact bytes.
    member this.Sha256 =
        let (ArtifactRevision(_, sha256, _)) = this
        sha256

    /// Optional Git resolver metadata for a commit containing the artifact.
    member this.Commit =
        let (ArtifactRevision(_, _, commit)) = this
        commit

/// Construction and verification helpers for immutable artifact occurrences.
[<RequireQualifiedAccess>]
module ArtifactRevision =

    let private isHex character =
        (character >= '0' && character <= '9')
        || (character >= 'a' && character <= 'f')
        || (character >= 'A' && character <= 'F')

    let private isSha256 (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length = 64
        && (value |> Seq.forall isHex)

    let private digest (bytes: byte array) =
        use sha256 = SHA256.Create()

        sha256.ComputeHash bytes
        |> Array.map (fun (value: byte) -> value.ToString("x2"))
        |> String.concat ""

    /// Attempts to construct a validated artifact revision.
    let tryCreate path sha256 commit =
        if String.IsNullOrWhiteSpace path
           || not (isSha256 sha256)
           || (commit |> Option.exists String.IsNullOrWhiteSpace) then
            None
        else
            Some(ArtifactRevision(path, sha256.ToLowerInvariant(), commit))

    /// Constructs a validated artifact revision or raises `ArgumentException`.
    let create path sha256 commit =
        if String.IsNullOrWhiteSpace path then
            invalidArg (nameof path) "Artifact path cannot be empty."
        elif not (isSha256 sha256) then
            invalidArg (nameof sha256) "SHA-256 must contain exactly 64 hexadecimal digits."
        elif commit |> Option.exists String.IsNullOrWhiteSpace then
            invalidArg (nameof commit) "Commit metadata must be absent or non-empty."
        else
            ArtifactRevision(path, sha256.ToLowerInvariant(), commit)

    /// Constructs an artifact revision by hashing the exact supplied bytes.
    let ofBytes path commit (bytes: byte array) =
        if isNull bytes then
            nullArg (nameof bytes)

        create path (digest bytes) commit

    /// Tests whether the supplied bytes have the revision's declared SHA-256 digest.
    let verifyBytes (revision: ArtifactRevision) (bytes: byte array) =
        if isNull bytes then
            false
        else
            String.Equals(revision.Sha256, digest bytes, StringComparison.Ordinal)

/// A format-qualified selector for one fragment inside an artifact.
type FragmentSelector =
    {
        /// The specification defining the selector syntax and evaluation rules.
        ConformsTo: Iri
        /// The selector text, including any fragment marker required by its format.
        Value: string
    }

/// An immutable artifact occurrence paired with a selector into its bytes.
type FragmentRef =
    {
        /// The exact artifact occurrence containing the selected fragment.
        Artifact: ArtifactRevision
        /// The selector designating the fragment within the artifact.
        Selector: FragmentSelector
    }
