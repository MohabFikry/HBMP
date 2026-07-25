namespace Mersal.Emr.Domain;

/// <summary>Read/interop projection of the canonical EMR tables onto FHIR R4 resource shapes
/// (phase-4 §4.1: encounter→Encounter, diagnosis→Condition, vital→Observation, allergy→AllergyIntolerance,
/// medication_history→MedicationStatement). This is a projection ONLY — the canonical model is not forked.
/// The shapes are intentionally minimal (resourceType + the fields interop consumers need); a full FHIR
/// serializer/façade is phase 13.</summary>
public static class FhirProjection
{
    private const string IcdSystem = "http://hl7.org/fhir/sid/icd-10";
    private const string LoincSystem = "http://loinc.org";

    public static object Encounter(Encounter e) => new
    {
        resourceType = "Encounter",
        id = e.EncounterId,
        identifier = new[] { new { system = "urn:mersal:encounter-no", value = e.EncounterNo } },
        status = e.Status switch
        {
            EncounterStatus.InProgress => "in-progress",
            EncounterStatus.Completed => "finished",
            _ => "cancelled",
        },
        subject = new { reference = $"Patient/{e.BeneficiaryId}" },
        period = new { start = e.StartedAt },
    };

    public static object Condition(Diagnosis d) => new
    {
        resourceType = "Condition",
        id = d.DiagnosisId,
        clinicalStatus = new { coding = new[] { new { code = d.ClinicalStatus.ToString().ToLowerInvariant() } } },
        code = new { coding = new[] { new { system = IcdSystem, code = d.IcdCode } } },
        encounter = new { reference = $"Encounter/{d.EncounterId}" },
        rank = d.DiagnosisRank.ToString(),
        recordedDate = d.RecordedAt,
    };

    public static object Observation(Vital v) => new
    {
        resourceType = "Observation",
        id = v.VitalId,
        status = "final",
        category = "vital-signs",
        code = v.LoincCode is null
            ? (object)new { text = v.VitalType.ToString() }
            : new { coding = new[] { new { system = LoincSystem, code = v.LoincCode } }, text = v.VitalType.ToString() },
        valueQuantity = new { value = v.ValueNum, unit = v.Unit ?? VitalRange.CanonicalUnit(v.VitalType) },
        encounter = new { reference = $"Encounter/{v.EncounterId}" },
        effectiveDateTime = v.MeasuredAt,
    };

    public static object AllergyIntolerance(Allergy a) => new
    {
        resourceType = "AllergyIntolerance",
        id = a.AllergyId,
        clinicalStatus = new { coding = new[] { new { code = a.Status.ToString().ToLowerInvariant() } } },
        criticality = a.Severity switch
        {
            AllergySeverity.Severe => "high",
            AllergySeverity.Moderate => "unable-to-assess",
            _ => "low",
        },
        code = new { coding = new[] { new { system = "urn:mersal:allergen", code = a.AllergenId.ToString() } } },
        patient = new { reference = $"Patient/{a.BeneficiaryId}" },
        reaction = a.Reaction,
    };

    public static object MedicationStatement(MedicationHistory m) => new
    {
        resourceType = "MedicationStatement",
        id = m.MedHistoryId,
        status = m.Status == MedicationStatus.Active ? "active" : "stopped",
        medicationReference = new { reference = $"Medication/{m.DrugId}" },
        subject = new { reference = $"Patient/{m.BeneficiaryId}" },
        effectivePeriod = new { start = m.StartDate, end = m.EndDate },
        informationSource = m.Source.ToString(),
    };
}
