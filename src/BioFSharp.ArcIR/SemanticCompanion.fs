namespace BioFSharp.ArcIR

open System.Security.Cryptography
open System.Text

/// Shared deterministic identity rules for additive semantic companion assertions.
module internal SemanticCompanion =

    [<Literal>]
    let IdentityBase = "urn:biofsharp:arcir:mapped:"

    let private sha256 (value: string) =
        use algorithm = SHA256.Create()

        algorithm.ComputeHash(Encoding.UTF8.GetBytes value)
        |> Array.map (fun byte -> byte.ToString("x2"))
        |> String.concat ""

    let isId (id: Iri) = id.Value.StartsWith(IdentityBase)

    let id (owner: Iri) (input: Iri) (role: string) (target: Iri) =
        Iri.Create(IdentityBase + sha256 (owner.Value + "\n" + input.Value + "\n" + role + "\n" + target.Value))
