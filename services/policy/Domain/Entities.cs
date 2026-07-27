namespace Mersal.Policy.Domain;

// Policy domain per 15-database-erd §5 + 22-data-dictionary. Cross-service beneficiary_id is a
// logical reference (value), never an enforced cross-schema FK.

public enum PolicyStatus { Active, Suspended, Expired }
public enum CoverageStatus { Active, Suspended, Expired }
public enum LimitType { Annual, PerEncounter, Lifetime, Count }
public enum ResetPeriod { None, Monthly, Quarterly, Yearly }

public sealed class Policy
{
    public Guid PolicyId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public string PolicyNo { get; set; } = default!;
    public string? Sponsor { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public PolicyStatus Status { get; set; } = PolicyStatus.Active;

    /// <summary>19.2 — the payer this contract is with. Replaces the free-text <see cref="Sponsor"/>, which is
    /// kept only so existing rows stay readable until the 19.7 backfill retires it.</summary>
    public Guid? PayerId { get; set; }
    /// <summary>The policy this one renewed, forming the renewal chain a member's history is read along.</summary>
    public Guid? PreviousPolicyId { get; set; }
    public int? MaxMembers { get; set; }

    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class BenefitCategory
{
    public Guid BenefitCategoryId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public string Code { get; set; } = default!;   // LAB|IMAGING|PHARMACY|CONSULT|REFERRAL
    public string Name { get; set; } = default!;
}

public sealed class Coverage
{
    public Guid CoverageId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid PolicyId { get; set; }
    public Guid BeneficiaryId { get; set; }         // logical FK (value)
    public Guid BenefitCategoryId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public CoverageStatus Status { get; set; } = CoverageStatus.Active;

    /// <summary>19.2 — the plan version this entitlement was GENERATED from, and the enrolment that produced
    /// it. Provenance is what makes "why am I covered for this, and for how much" answerable: both point back
    /// to a dated, immutable configuration rather than to whoever typed the row.</summary>
    public Guid? SourcePlanVersionId { get; set; }
    public Guid? EnrollmentId { get; set; }

    public bool IsDeleted { get; set; }
    public List<CoverageLimit> Limits { get; set; } = [];
}

public sealed class CoverageLimit
{
    public Guid CoverageLimitId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid CoverageId { get; set; }
    public LimitType LimitType { get; set; }
    public decimal LimitValue { get; set; }
    /// <summary>Authoritative accumulator — source of truth for benefit usage. Starts 0; only
    /// incremented by consume/dispense sagas (later phases). Read-only here except resets.</summary>
    public decimal ConsumedValue { get; set; }
    public string CurrencyCode { get; set; } = "EGP";
    public ResetPeriod ResetPeriod { get; set; } = ResetPeriod.None;
    public DateOnly? LastResetOn { get; set; }

    public decimal Remaining => LimitValue - ConsumedValue;
}
