namespace Mersal.Eligibility.Infrastructure;

// Derived READ MODELS owned by eligibility-service, kept in sync by consuming patient/policy events.
// None of these is a source of truth — they are cache/read-optimized projections that are invalidated
// and recomputed on the upstream events (19-audit-strategy, 16-service-architecture).

/// <summary>Minimum-necessary member facts: status + identity for eligibility + reception search.
/// Carries NO clinical/EMR data (11-permission-matrix): reception ≠ EMR.</summary>
public sealed class MemberProjection
{
    public Guid BeneficiaryId { get; set; }
    public string? MemberNo { get; set; }
    public string GivenName { get; set; } = "";
    public string FamilyName { get; set; } = "";
    public string Status { get; set; } = "Pending";        // MemberStatus name
    public string? PrimaryPhone { get; set; }
    public string? NationalId { get; set; }
    public string? Passport { get; set; }
    public string? RefugeeId { get; set; }
    public string? UnhcrNo { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A coverage + its limits (limits denormalized as JSON) for the decision engine.</summary>
public sealed class CoverageProjection
{
    public Guid CoverageId { get; set; }
    public Guid BeneficiaryId { get; set; }
    public string BenefitCategory { get; set; } = "";
    public string PolicyNo { get; set; } = "";
    public string Status { get; set; } = "Active";
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public string LimitsJson { get; set; } = "[]";         // List<LimitStateDto>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A persisted, derived eligibility decision. Cached in Valkey; invalidated by events.</summary>
public sealed class EligibilitySnapshot
{
    public Guid SnapshotId { get; set; }
    public Guid BeneficiaryId { get; set; }
    public string BenefitCategory { get; set; } = "";
    public string Decision { get; set; } = "";
    public Guid? CoverageId { get; set; }
    public string LimitStateJson { get; set; } = "null";
    public string ReasonsJson { get; set; } = "[]";
    public string VersionHash { get; set; } = "";
    public DateTimeOffset ComputedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Dedupe ledger so at-least-once event redelivery is a no-op (consumers are idempotent).</summary>
public sealed class ProcessedEvent
{
    public Guid EventId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
