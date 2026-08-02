namespace Mersal.Emr.Domain;

// Clinical documentation entities (22-data-dictionary §6.3–6.7). Enums use the canonical sets EXACTLY.

public enum NoteType { SOAP, Progress, Nursing }
public enum DiagnosisRank { Primary, Secondary }
public enum ClinicalStatus { Active, Resolved, Recurrence }
/// <summary>Vital observation types (§6.5). <c>BP</c> is the SYSTOLIC value and <c>BPDiastolic</c> its
/// partner — a blood pressure is two rows on one encounter, read as a pair (migration 0017). Appended rather
/// than inserted in clinical order so no persisted ordinal shifts.</summary>
public enum VitalType { BP, HR, Temp, SpO2, Weight, Height, BMI, BPDiastolic }
public enum AllergySeverity { Mild, Moderate, Severe }
public enum AllergyStatus { Active, Inactive, Resolved }
public enum MedicationSource { Prescribed, SelfReported, External }
public enum MedicationStatus { Active, Stopped }

/// <summary>SOAP / Progress / Nursing note (§6.3). Signing sets <see cref="IsSigned"/> and LOCKS the note
/// (immutable thereafter — corrections are made with an <see cref="AddendumOfNoteId"/> note, never in place).
/// An unsigned note is editable by its author only.</summary>
public sealed class EmrNote
{
    public Guid NoteId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid EncounterId { get; set; }
    public NoteType NoteType { get; set; } = NoteType.SOAP;
    public string? Subjective { get; set; }
    public string? Objective { get; set; }
    public string? Assessment { get; set; }
    public string? Plan { get; set; }
    public Guid? AddendumOfNoteId { get; set; }   // set when this note is an addendum correcting a signed note
    public string AuthoredBy { get; set; } = default!;
    public DateTimeOffset AuthoredAt { get; set; }
    public bool IsSigned { get; set; }
    public DateTimeOffset? SignedAt { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>Coded diagnosis (§6.4). <see cref="IcdCode"/> MUST exist in masterdata.icd_code.</summary>
public sealed class Diagnosis
{
    public Guid DiagnosisId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid EncounterId { get; set; }
    public string IcdCode { get; set; } = default!;
    public DiagnosisRank DiagnosisRank { get; set; } = DiagnosisRank.Primary;
    public ClinicalStatus ClinicalStatus { get; set; } = ClinicalStatus.Active;
    public string RecordedBy { get; set; } = default!;
    public DateTimeOffset RecordedAt { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>Vital observation (§6.5). <see cref="ValueNum"/> is validated against a per-type range;
/// <see cref="LoincCode"/> is optional and, when present, validated against masterdata LOINC.</summary>
public sealed class Vital
{
    public Guid VitalId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid EncounterId { get; set; }
    public VitalType VitalType { get; set; }
    public decimal? ValueNum { get; set; }
    public string? Unit { get; set; }
    public string? LoincCode { get; set; }
    public string RecordedBy { get; set; } = default!;
    public DateTimeOffset MeasuredAt { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>Allergy (§6.6), held at the beneficiary level. <see cref="AllergenId"/> → masterdata.allergen.</summary>
public sealed class Allergy
{
    public Guid AllergyId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid BeneficiaryId { get; set; }
    public Guid AllergenId { get; set; }
    public string? Reaction { get; set; }
    public AllergySeverity Severity { get; set; } = AllergySeverity.Mild;
    public AllergyStatus Status { get; set; } = AllergyStatus.Active;
    public string RecordedBy { get; set; } = default!;
    public DateTimeOffset RecordedAt { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>Medication history (§6.7), held at the beneficiary level. <see cref="DrugId"/> → masterdata.drug.</summary>
public sealed class MedicationHistory
{
    public Guid MedHistoryId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid BeneficiaryId { get; set; }
    public Guid DrugId { get; set; }
    public MedicationSource Source { get; set; } = MedicationSource.SelfReported;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public MedicationStatus Status { get; set; } = MedicationStatus.Active;
    public string RecordedBy { get; set; } = default!;
    public DateTimeOffset RecordedAt { get; set; }
    public bool IsDeleted { get; set; }
}
