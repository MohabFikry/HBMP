using System.Text.Json;
using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

/// <summary>Contract test for the FHIR R4 read projection (phase-4 §4.1): the canonical tables map onto the
/// expected FHIR resourceTypes with the coding systems interop consumers rely on.</summary>
public class FhirProjectionTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o, JsonOpts);

    [Fact]
    public void Encounter_maps_to_fhir_Encounter_with_status()
    {
        var e = new Encounter { EncounterId = Guid.NewGuid(), EncounterNo = "ENC-2026-000001", BeneficiaryId = Guid.NewGuid(), Status = EncounterStatus.InProgress };
        var f = Json(FhirProjection.Encounter(e));
        f.GetProperty("resourceType").GetString().Should().Be("Encounter");
        f.GetProperty("status").GetString().Should().Be("in-progress");
        f.GetProperty("subject").GetProperty("reference").GetString().Should().Be($"Patient/{e.BeneficiaryId}");
    }

    [Fact]
    public void Diagnosis_maps_to_Condition_with_icd10_system()
    {
        var d = new Diagnosis { DiagnosisId = Guid.NewGuid(), EncounterId = Guid.NewGuid(), IcdCode = "J06.9", DiagnosisRank = DiagnosisRank.Primary, ClinicalStatus = ClinicalStatus.Active };
        var f = Json(FhirProjection.Condition(d));
        f.GetProperty("resourceType").GetString().Should().Be("Condition");
        f.GetProperty("code").GetProperty("coding")[0].GetProperty("system").GetString().Should().Contain("icd-10");
        f.GetProperty("code").GetProperty("coding")[0].GetProperty("code").GetString().Should().Be("J06.9");
    }

    [Fact]
    public void Vital_maps_to_Observation_with_quantity()
    {
        var v = new Vital { VitalId = Guid.NewGuid(), EncounterId = Guid.NewGuid(), VitalType = VitalType.HR, ValueNum = 72m, Unit = "bpm" };
        var f = Json(FhirProjection.Observation(v));
        f.GetProperty("resourceType").GetString().Should().Be("Observation");
        f.GetProperty("valueQuantity").GetProperty("value").GetDecimal().Should().Be(72m);
    }

    [Fact]
    public void Allergy_maps_to_AllergyIntolerance_with_criticality()
    {
        var a = new Allergy { AllergyId = Guid.NewGuid(), BeneficiaryId = Guid.NewGuid(), AllergenId = Guid.NewGuid(), Severity = AllergySeverity.Severe, Status = AllergyStatus.Active };
        var f = Json(FhirProjection.AllergyIntolerance(a));
        f.GetProperty("resourceType").GetString().Should().Be("AllergyIntolerance");
        f.GetProperty("criticality").GetString().Should().Be("high");
    }

    [Fact]
    public void MedicationHistory_maps_to_MedicationStatement()
    {
        var m = new MedicationHistory { MedHistoryId = Guid.NewGuid(), BeneficiaryId = Guid.NewGuid(), DrugId = Guid.NewGuid(), Status = MedicationStatus.Active, Source = MedicationSource.Prescribed };
        var f = Json(FhirProjection.MedicationStatement(m));
        f.GetProperty("resourceType").GetString().Should().Be("MedicationStatement");
        f.GetProperty("status").GetString().Should().Be("active");
    }
}
