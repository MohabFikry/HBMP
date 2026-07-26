using FluentAssertions;
using Mersal.Authz;
using Mersal.Interop.Api;
using Mersal.Interop.Domain.Fhir;
using Mersal.Interop.Domain.Mapping;
using Mersal.Interop.Domain.Model;

namespace Mersal.Interop.Tests;

/// <summary>
/// The FHIR R4 conformance harness (13.3): representative resources produced by the mappers validate against the
/// structural R4 rules; the CapabilityStatement advertises EXACTLY the implemented interactions; and a
/// min-necessary parity check proves the façade is not a bypass.
/// </summary>
public class FhirConformanceTests
{
    [Fact]
    public void Representative_resources_all_validate_against_R4()
    {
        R4Conformance.Validate(FhirMappers.Patient(new BeneficiarySource("MRS-M-1",
            [new SourceIdentifier("NationalID", "29001011234567")], "Hassan", "Amal", new DateOnly(1990, 1, 1), "female",
            [new SourceTelecom("phone", "+20100000000", "mobile")], []))).Should().BeEmpty();

        R4Conformance.Validate(FhirMappers.Coverage(new CoverageSource("COV-1", "MRS-M-1", "Active", "Mersal", "plan", "Gold",
            [], [new CoverageLimit("Outpatient", 10000m, 7500m)]))).Should().BeEmpty();

        R4Conformance.Validate(FhirMappers.ServiceRequest(new ServiceRequestSource("SR-1", "Approved", "order", "laboratory",
            new CodedConcept(Fhir.Systems.Cpt, "80053", "Metabolic panel"), 1m, "each", "MRS-M-1", "PR-1", "ORG-1"))).Should().BeEmpty();

        R4Conformance.Validate(FhirMappers.MedicationRequest(new MedicationRequestSource("MR-1", "Active",
            new CodedConcept(Fhir.Systems.Atc, "A10BA02", "Metformin"), "500mg BID", 30m, "tablet", "MRS-M-1", "PR-1"))).Should().BeEmpty();

        R4Conformance.Validate(FhirMappers.DiagnosticReport(new DiagnosticReportSource("DR-1", "Final",
            new CodedConcept(Fhir.Systems.Loinc, "24323-8", "Panel"), "MRS-M-1", "SR-1", DateTimeOffset.UtcNow, "application/pdf", "Result"))).Should().BeEmpty();

        R4Conformance.Validate(FhirMappers.Encounter(new EncounterSource("ENC-1", "Completed", "AMB",
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, "MRS-M-1", "PR-1"))).Should().BeEmpty();

        R4Conformance.Validate(FhirMappers.Condition(new ConditionSource("D-1", "Active",
            new CodedConcept(Fhir.Systems.Icd10, "E11.9", "Type 2 diabetes"), "MRS-M-1", "ENC-1", DateTimeOffset.UtcNow))).Should().BeEmpty();

        R4Conformance.Validate(FhirMappers.Observation(new ObservationSource("O-1", "Final", "vital-signs",
            new CodedConcept(Fhir.Systems.Loinc, "8867-4", "Heart rate"), 72m, "beats/minute", "/min", "MRS-M-1", "ENC-1", DateTimeOffset.UtcNow))).Should().BeEmpty();

        R4Conformance.Validate(FhirMappers.AllergyIntolerance(new AllergyIntoleranceSource("A-1",
            new CodedConcept("http://snomed.info/sct", "227493005", "Cashew"), "High", "Anaphylaxis", "MRS-M-1"))).Should().BeEmpty();
    }

    [Fact]
    public void Invalid_resources_are_flagged()
    {
        var badRef = FhirMappers.Condition(new ConditionSource("D-1", "Active",
            new CodedConcept(Fhir.Systems.Icd10, "E11.9", null), "MRS-M-1", null, null));
        badRef["subject"] = new System.Text.Json.Nodes.JsonObject { ["reference"] = "Beneficiary/MRS-M-1" }; // wrong type
        R4Conformance.Validate(badRef).Should().Contain(i => i.Contains("must reference Patient"));

        var noCode = new System.Text.Json.Nodes.JsonObject { ["resourceType"] = "Observation", ["status"] = "final", ["subject"] = new System.Text.Json.Nodes.JsonObject { ["reference"] = "Patient/1" } };
        R4Conformance.Validate(noCode).Should().Contain(i => i.Contains("Observation.code is required"));
    }

    [Fact]
    public void CapabilityStatement_matches_the_implemented_interactions_exactly()
    {
        var stmt = FhirCapability.Statement("https://x/fhir/r4");
        var advertised = stmt["rest"]!.AsArray()[0]!["resource"]!.AsArray();

        // Every advertised resource + its create flag must equal the registry the endpoints are wired from.
        advertised.Count.Should().Be(FhirCapability.Resources.Count);
        foreach (var r in FhirCapability.Resources)
        {
            var node = advertised.First(x => x!["type"]!.GetValue<string>() == r.Name)!;
            var interactions = node["interaction"]!.AsArray().Select(i => i!["code"]!.GetValue<string>()).ToList();
            interactions.Should().Contain(["read", "search-type"]);
            interactions.Contains("create").Should().Be(r.CanCreate, $"{r.Name} create advertisement must match implementation");
        }
    }

    [Fact]
    public void Min_necessary_parity_a_role_blind_to_diagnosis_natively_cannot_read_Condition_via_fhir()
    {
        // The façade reuses the SAME bundle as native — Finance has no Condition rule → default-deny.
        var bundle = InteropPolicies.Bundle();
        bundle.Match(InteropPolicies.ReadAction("Condition"), InteropPolicies.Resource)!
            .Roles.Should().NotContain("finance").And.Contain("doctor");
    }
}
