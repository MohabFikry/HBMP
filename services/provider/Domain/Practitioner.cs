namespace Mersal.Provider.Domain;

// Phase 14.5 — structured clinician identity (design 37 §4). A practitioner is the clinical profile behind a
// user (logical FK to identity). Specialty is reference data; a practitioner has one-or-many specialties (one
// primary) and serves one-or-many branches. Specialty drives referral routing, reporting, and default
// examination-type suggestions — Psychiatry / Clinical Psychology feed the sensitivity defaults (14.6).

public enum PractitionerType { Doctor, Nurse }

public enum PractitionerStatus { Active, Suspended, Inactive }

/// <summary>Reference specialty (design 37 §4). <c>ParentCode</c> allows a shallow taxonomy.</summary>
public sealed class Specialty
{
    public string SpecialtyCode { get; set; } = default!;   // GP, PSYCH, CPSY, … (UK)
    public string NameEn { get; set; } = default!;
    public string NameAr { get; set; } = default!;
    public string? ParentCode { get; set; }
    public bool IsDeleted { get; set; }
}

public sealed class Practitioner
{
    public Guid PractitionerId { get; set; }
    public string TenantId { get; set; } = default!;
    public string UserId { get; set; } = default!;          // logical FK to identity (value, not FK)
    public PractitionerType PractitionerType { get; set; }
    public string FullNameEn { get; set; } = default!;
    public string FullNameAr { get; set; } = default!;
    public string? LicenseNo { get; set; }
    public DateOnly? LicenseExpiry { get; set; }            // feeds the 2b credential-expiry sweep
    public PractitionerStatus Status { get; set; } = PractitionerStatus.Active;
    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<PractitionerSpecialty> Specialties { get; set; } = [];
    public List<PractitionerBranchAssignment> BranchAssignments { get; set; } = [];
}

/// <summary>Many-to-many practitioner↔specialty; exactly one flagged primary (partial-unique in the migration).</summary>
public sealed class PractitionerSpecialty
{
    public Guid PractitionerId { get; set; }
    public string SpecialtyCode { get; set; } = default!;
    public bool IsPrimary { get; set; }
}

/// <summary>A practitioner may serve one or many branches (design 37 §4). Booking/availability at an
/// unassigned branch is rejected 422 (validated at both availability creation and booking).</summary>
public sealed class PractitionerBranchAssignment
{
    public Guid AssignmentId { get; set; }
    public Guid PractitionerId { get; set; }
    public Guid BranchId { get; set; }
    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
    public string Status { get; set; } = "Active";          // Active | Revoked
}
