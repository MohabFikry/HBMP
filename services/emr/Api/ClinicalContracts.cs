using Mersal.Emr.Domain;

namespace Mersal.Emr.Api;

// ---- Phase 4.1 clinical documentation requests (17-api-specifications §6–7, US-030/US-031) ----

public sealed record CreateNoteRequest(NoteType NoteType, string? Subjective, string? Objective, string? Assessment, string? Plan);
public sealed record UpdateNoteRequest(string? Subjective, string? Objective, string? Assessment, string? Plan);
public sealed record AddDiagnosisRequest(string IcdCode, DiagnosisRank DiagnosisRank, ClinicalStatus ClinicalStatus);
public sealed record AddVitalRequest(VitalType VitalType, decimal? ValueNum, string? Unit, string? LoincCode, DateTimeOffset? MeasuredAt);
public sealed record AddAllergyRequest(Guid AllergenId, string? Reaction, AllergySeverity Severity, AllergyStatus Status);
public sealed record AddMedicationHistoryRequest(Guid DrugId, MedicationSource Source, DateOnly? StartDate, DateOnly? EndDate, MedicationStatus Status);
/// <summary>Set a beneficiary's blood group (migration 0021). One of <see cref="BloodGroups.All"/>.</summary>
public sealed record SetBloodGroupRequest(string BloodGroup);

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

/// <summary>
/// A recorded allergy, with the substance NAMED.
///
/// <para><see cref="AllergenDisplay"/> is not decoration. profile-service's alerts provider has always read
/// this field to build the patient context bar's chips, and until migration 0020 nothing ever sent it — so
/// the strip fell back to <see cref="AllergenId"/> and would have shown a clinician a uuid where the
/// substance belongs. It is null only for rows recorded before 0020; readers say "(unspecified)".</para>
/// </summary>
public sealed record AllergyResponse(
    Guid AllergyId, Guid BeneficiaryId, Guid AllergenId, string? AllergenDisplay,
    string? Reaction, string Severity, string Status)
{
    public static AllergyResponse From(Allergy a) => new(
        a.AllergyId, a.BeneficiaryId, a.AllergenId, a.AllergenDisplay,
        a.Reaction, a.Severity.ToString(), a.Status.ToString());
}

public sealed record MedicationHistoryResponse(
    Guid MedHistoryId, Guid BeneficiaryId, Guid DrugId, string Source, DateOnly? StartDate, DateOnly? EndDate, string Status)
{
    public static MedicationHistoryResponse From(MedicationHistory m) => new(
        m.MedHistoryId, m.BeneficiaryId, m.DrugId, m.Source.ToString(), m.StartDate, m.EndDate, m.Status.ToString());
}

/// <summary>
/// The standing clinical facts a reader needs BEFORE acting on a patient: blood group and the allergy list.
///
/// <para>One response, because it is one gate check and therefore one PHI-read audit event. Fetching the two
/// separately would double the treating-relationship check and make a clinician's single glance at a patient
/// look like two accesses in the review — the same reasoning that already binds history and encounters into
/// one call in profile-service's ClinicalContextSource.</para>
///
/// <para><see cref="BloodGroup"/> is null when nobody has recorded one. That is a real and common state and
/// must never be collapsed into a blank beside seven populated facts.</para>
/// </summary>
public sealed record MemberClinicalRecordResponse(
    Guid BeneficiaryId,
    string? BloodGroup,
    DateTimeOffset? BloodGroupRecordedAt,
    IReadOnlyList<AllergyResponse> Allergies);

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
