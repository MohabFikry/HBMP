using FluentAssertions;
using Mersal.Interop.Domain.Fhir;
using Mersal.Interop.Domain.Mapping;
using Mersal.Interop.Domain.Model;

namespace Mersal.Interop.Tests;

/// <summary>Pure HBMP → FHIR R4 mapping tests (no host, no DB) — the mapping is the heart of the façade.</summary>
public class FhirMappingTests
{
    [Fact]
    public void Patient_maps_identifiers_name_and_gender_with_typed_systems()
    {
        var src = new BeneficiarySource("MRS-M-1", [
            new SourceIdentifier("NationalID", "29001011234567"),
            new SourceIdentifier("UNHCRNo", "C-777"),
        ], "Hassan", "Amal", new DateOnly(1990, 1, 1), "female",
            [new SourceTelecom("phone", "+20100000000", "mobile")], []);

        var p = FhirMappers.Patient(src);

        p["resourceType"]!.GetValue<string>().Should().Be("Patient");
        p["gender"]!.GetValue<string>().Should().Be("female");
        p["birthDate"]!.GetValue<string>().Should().Be("1990-01-01");
        var ids = p["identifier"]!.AsArray();
        ids.Should().HaveCount(2);
        ids[0]!["system"]!.GetValue<string>().Should().Be(Fhir.IdentifierSystems.NationalId);
        ids[1]!["system"]!.GetValue<string>().Should().Be(Fhir.IdentifierSystems.UnhcrNo);
    }

    [Fact]
    public void ServiceRequest_status_maps_per_12_1_table()
    {
        var draft = FhirMappers.ServiceRequest(Sr("PendingApproval"));
        draft["status"]!.GetValue<string>().Should().Be("draft");
        FhirMappers.ServiceRequest(Sr("Approved"))["status"]!.GetValue<string>().Should().Be("active");
        FhirMappers.ServiceRequest(Sr("Completed"))["status"]!.GetValue<string>().Should().Be("completed");
        FhirMappers.ServiceRequest(Sr("Cancelled"))["status"]!.GetValue<string>().Should().Be("revoked");
        FhirMappers.ServiceRequest(Sr("Expired"))["status"]!.GetValue<string>().Should().Be("revoked");
    }

    [Fact]
    public void Referral_ServiceRequest_carries_referral_category()
    {
        var referral = FhirMappers.ServiceRequest(Sr("Approved", intent: "referral", category: "referral"));
        referral["category"]!.AsArray()[0]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>().Should().Be("306206005");
    }

    [Fact]
    public void Condition_carries_icd_code_and_clinical_status()
    {
        var c = FhirMappers.Condition(new ConditionSource("D-1", "Active",
            new CodedConcept(Fhir.Systems.Icd10, "E11.9", "Type 2 diabetes"), "MRS-M-1", "ENC-1", DateTimeOffset.UtcNow));
        c["resourceType"]!.GetValue<string>().Should().Be("Condition");
        c["clinicalStatus"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>().Should().Be("active");
        c["code"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>().Should().Be("E11.9");
        c["subject"]!["reference"]!.GetValue<string>().Should().Be("Patient/MRS-M-1");
    }

    [Fact]
    public void Observation_vital_carries_loinc_and_valueQuantity()
    {
        var o = FhirMappers.Observation(new ObservationSource("O-1", "Final", "vital-signs",
            new CodedConcept(Fhir.Systems.Loinc, "8867-4", "Heart rate"), 72m, "beats/minute", "/min",
            "MRS-M-1", "ENC-1", DateTimeOffset.UtcNow));
        o["status"]!.GetValue<string>().Should().Be("final");
        o["valueQuantity"]!["value"]!.GetValue<decimal>().Should().Be(72m);
        o["code"]!["coding"]!.AsArray()[0]!["system"]!.GetValue<string>().Should().Be(Fhir.Systems.Loinc);
    }

    [Fact]
    public void Coverage_limits_map_to_extension()
    {
        var cov = FhirMappers.Coverage(new CoverageSource("COV-1", "MRS-M-1", "Active", "Mersal", "plan", "Gold",
            [], [new CoverageLimit("Outpatient", 10000m, 7500m)]));
        cov["status"]!.GetValue<string>().Should().Be("active");
        cov["extension"]!.AsArray()[0]!["extension"]!.AsArray().Count.Should().Be(3);
    }

    [Fact]
    public void SearchBundle_wraps_entries_with_fullUrl()
    {
        var b = Fhir.SearchBundle("https://x/fhir/r4", "Patient", [FhirMappers.Patient(Ben("MRS-M-9"))]);
        b["type"]!.GetValue<string>().Should().Be("searchset");
        b["total"]!.GetValue<int>().Should().Be(1);
        b["entry"]!.AsArray()[0]!["fullUrl"]!.GetValue<string>().Should().Be("https://x/fhir/r4/Patient/MRS-M-9");
    }

    private static ServiceRequestSource Sr(string status, string intent = "order", string? category = "laboratory") =>
        new("SR-1", status, intent, category, new CodedConcept(Fhir.Systems.Cpt, "80053", "Metabolic panel"),
            1m, "each", "MRS-M-1", "PR-1", "ORG-1");

    private static BeneficiarySource Ben(string id) =>
        new(id, [], "X", "Y", null, "male", [], []);
}
