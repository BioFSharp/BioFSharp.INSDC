namespace BioFSharp.ArcIR.Tests

open Xunit
open BioFSharp.ArcIR

type FieldAccountingTests() =

    let iri value = Iri.Create value

    let artifact name content =
        ArtifactRevision.ofBytes name None (Phase3Fixtures.utf8 content)

    let fragment revision value =
        { Artifact = revision
          Selector =
            { ConformsTo = iri "https://example.org/selectors/test"
              Value = value } }

    [<Fact>]
    member _.``diagnostic reports reject duplicate diagnostic identities`` () =
        let revision = artifact "source.xml" "source"
        let target = fragment revision "#one"

        let diagnostic =
            { Id = iri "urn:test:diagnostic:one"
              Code = iri "urn:test:diagnostic-code:one"
              Severity = DiagnosticSeverity.Warning
              Message = "warning"
              Targets = [ target ]
              Related = [] }

        Assert.Throws<System.ArgumentException>(fun () ->
            DiagnosticReport.create (iri "urn:test:report") [ diagnostic; diagnostic ]
            |> ignore)
        |> ignore

    [<Fact>]
    member _.``field accounting enforces one outcome per source occurrence`` () =
        let revision = artifact "source.xml" "source"
        let input = fragment revision "#one"
        let report = DiagnosticReport.empty (iri "urn:test:report")

        let entry =
            { RuleId = iri "urn:test:rule"
              Input = input
              Outcome = FieldAccountingOutcome.Ignored "reviewed omission" }

        Assert.Throws<System.ArgumentException>(fun () ->
            FieldAccounting.create report [ entry; entry ] |> ignore)
        |> ignore

    [<Fact>]
    member _.``emitted locations become artifact-qualified provenance bindings`` () =
        let sourceRevision = artifact "source.xml" "source"
        let outputRevision = artifact "state.arcir.json" "state"
        let input = fragment sourceRevision "#one"
        let output = Phase3Fixtures.simpleLocation

        let accounting =
            FieldAccounting.create
                (DiagnosticReport.empty (iri "urn:test:report"))
                [ { RuleId = iri "urn:test:rule"
                    Input = input
                    Outcome = FieldAccountingOutcome.Emitted [ output ] } ]

        let binding = FieldAccounting.qualifyEmitted outputRevision accounting |> List.exactlyOne

        Assert.Equal(input, binding.Input)
        Assert.Equal(iri "urn:test:rule", binding.RuleId)
        Assert.Equal<FragmentRef list>([ ArcIRJson.fragmentRef outputRevision output ], binding.Outputs)
