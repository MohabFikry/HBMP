using Mersal.Emr.Domain;

namespace Mersal.Emr.Api;

// ---- Phase 4.1 clinical documentation requests (17-api-specifications §6–7, US-030/US-031) ----

public sealed record CreateNoteRequest(NoteType NoteType, string? Subjective, string? Objective, string? Assessment, string? Plan);
public sealed record UpdateNoteRequest(string? Subjective, string? Objective, string? Assessment, string? Plan);
public sealed record AddDiagnosisRequest(string IcdCode, DiagnosisRank DiagnosisRank, ClinicalStatus ClinicalStatus);
public sealed record AddVitalRequest(VitalType VitalType, decimal? ValueNum, string? Unit, string? LoincCode, DateTimeOffset? MeasuredAt);
public sealed record AddAllergyRequest(Guid AllergenId, string? Reaction, AllergySeverity Severity, AllergyStatus Status);
public sealed record AddMedicationHistoryRequest(Guid DrugId, MedicationSource Source, DateOnly? StartDate, DateOnly? EndDate, MedicationStatus Status);

// ---- Responses ----

public sealed record NoteResponse(
    Guid NoteId, Guid EncounterId, string NoteType, string? Subjective, string? Objective, string? Assessment,
    string? Plan, Guid? AddendumOfNoteId, string AuthoredBy, DateTimeOffset AuthoredAt, bool IsSigned)
{
    public static NoteResponse From(EmrNote n) => new(
        n.NoteId, n.EncounterId, n.NoteType.ToString(), n.Subjective, n.Objective, n.Assessment, n.Plan,
        n.AddendumOfNoteId, n.AuthoredBy, n.AuthoredAt, n.IsSigned);
}

public sealed record DiagnosisResponse(
    Guid DiagnosisId, Guid EncounterId, string IcdCode, string DiagnosisRank, string ClinicalStatus, DateTimeOffset RecordedAt)
{
    public static DiagnosisResponse From(Diagnosis d) => new(
        d.DiagnosisId, d.EncounterId, d.IcdCode, d.DiagnosisRank.ToString(), d.ClinicalStatus.ToString(), d.RecordedAt);
}

public sealed record VitalResponse(
    Guid VitalId, Guid EncounterId, string VitalType, decimal? ValueNum, string? Unit, string? LoincCode, DateTimeOffset MeasuredAt)
{
    public static VitalResponse From(Vital v) => new(
        v.VitalId, v.EncounterId, v.VitalType.ToString(), v.ValueNum, v.Unit, v.LoincCode, v.MeasuredAt);
}

public sealed record AllergyResponse(
    Guid AllergyId, Guid BeneficiaryId, Guid AllergenId, string? Reaction, string Severity, string Status)
{
    public static AllergyResponse From(Allergy a) => new(
        a.AllergyId, a.BeneficiaryId, a.AllergenId, a.Reaction, a.Severity.ToString(), a.Status.ToString());
}

public sealed record MedicationHistoryResponse(
    Guid MedHistoryId, Guid BeneficiaryId, Guid DrugId, string Source, DateOnly? StartDate, DateOnly? EndDate, string Status)
{
    public static MedicationHistoryResponse From(MedicationHistory m) => new(
        m.MedHistoryId, m.BeneficiaryId, m.DrugId, m.Source.ToString(), m.StartDate, m.EndDate, m.Status.ToString());
}

/// <summary>The full clinical record for an encounter — returned only to a treating clinician (or the approval
/// team). Aggregates the summary + history so US-030 "I see summary, history, diagnoses, allergies, vitals, and
/// medication history" is one call.</summary>
public sealed record ClinicalRecordResponse(
    EncounterResponse Encounter,
    IReadOnlyList<NoteResponse> Notes,
    IReadOnlyList<DiagnosisResponse> Diagnoses,
    IReadOnlyList<VitalResponse> Vitals,
    IReadOnlyList<AllergyResponse> Allergies,
    IReadOnlyList<MedicationHistoryResponse> MedicationHistory);
