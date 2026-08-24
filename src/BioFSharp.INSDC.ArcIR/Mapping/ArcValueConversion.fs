namespace BioFSharp.INSDC.ArcIR

open System
open Arc.Build

/// Turns the CLR scalar values on the generated INSDC types into typed `ArcValue`s, dropping absent
/// (null / unspecified) values. This is what keeps the mapping faithful — a real `Integer`/`Boolean`/
/// `DateTime` rather than everything stuffed into a string, which is the whole reason we map from the
/// typed objects instead of the flat decompilation.
[<RequireQualifiedAccess>]
module ArcValueConversion =

    /// `Some (String s)` for a non-blank string; `None` for null/whitespace (an absent XML value).
    let ofString (value: string) : ArcValue option =
        if String.IsNullOrWhiteSpace value then None else Some(ArcValue.String value)

    /// Converts a Boolean to a typed ArcIR value.
    let ofBool (value: bool) : ArcValue = ArcValue.Boolean value

    /// Converts a 32-bit integer to the graph's 64-bit integer value.
    let ofInt (value: int) : ArcValue = ArcValue.Integer(int64 value)

    /// Converts a 64-bit integer to a typed ArcIR value.
    let ofInt64 (value: int64) : ArcValue = ArcValue.Integer value

    /// Converts a double-precision value to a typed ArcIR value.
    let ofFloat (value: float) : ArcValue = ArcValue.Float value

    /// A CLR `DateTime` as an `ArcValue.DateTime` (carrying the value's own offset).
    let ofDateTime (value: DateTime) : ArcValue = ArcValue.DateTime(DateTimeOffset value)

    /// `Some` only when the nullable actually carries a value.
    let ofNullableDateTime (value: Nullable<DateTime>) : ArcValue option =
        if value.HasValue then Some(ofDateTime value.Value) else None

    /// Base IRI generated enum members are minted under, namespaced by enum type: `<base><EnumType>/<Member>`.
    /// INSDC enums are closed vocabularies, so each member gets a stable IRI (not a bare token string).
    [<Literal>]
    let EnumBaseIri = "http://purl.org/arc/insdc/enum#"

    /// A boxed generated enum value as an `ArcValue.Iri`; `None` for a null box. Reflection-based, for enum
    /// values reached generically (e.g. the chosen `Platform` instrument model).
    let ofEnumObj (value: obj) : ArcValue option =
        match value with
        | null -> None
        | v -> Some(ArcValue.Iri(Iri.Create(EnumBaseIri + v.GetType().Name + "/" + string v)))

    /// A typed generated enum value as an `ArcValue.Iri`.
    let ofEnum (value: 'e when 'e: enum<int>) : ArcValue =
        (ofEnumObj (box value)).Value

    /// `Some (Iri name, value)` when the string is present; `None` otherwise — for building the
    /// `Properties` bag while silently dropping absent fields.
    let stringProp (name: string) (value: string) : (Iri * ArcValue) option =
        ofString value |> Option.map (fun v -> Iri.Create name, v)
