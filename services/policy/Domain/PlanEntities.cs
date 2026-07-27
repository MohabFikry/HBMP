namespace Mersal.Policy.Domain;

// Phase 19.1 — the PAS product layer (design 38 §3). payer → plan → effective-dated plan_version →
// benefit_rule. Cross-service ids stay logical values; the only FKs are inside the policy schema.

public enum PayerType { SelfFunded, Donor, Government, PartnerNGO, Insurer }
public enum CatalogStatus { Active, Inactive }

/// <summary>Lifecycle of a benefit configuration. <c>Draft</c> is freely editable; <c>Active</c> is in force and
/// IMMUTABLE; <c>Superseded</c> was replaced by a later version but still resolves for service dates inside its
/// own window; <c>Retired</c> was withdrawn without a successor and likewise still resolves for the past.</summary>
public enum PlanVersionStatus { Draft, Active, Superseded, Retired }

public sealed class Payer
{
    public Guid PayerId { get; set; }
    public string TenantId { get; set; } = "";
    public string PayerCode { get; set; } = default!;
    public string NameEn { get; set; } = default!;
    public string NameAr { get; set; } = default!;
    public PayerType PayerType { get; set; }
    /// <summary>Free-form contact block (jsonb). Operational contact detail, not beneficiary PII.</summary>
    public string Contact { get; set; } = "{}";
    public CatalogStatus Status { get; set; } = CatalogStatus.Active;
    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public sealed class Plan
{
    public Guid PlanId { get; set; }
    public string TenantId { get; set; } = "";
    public string PlanCode { get; set; } = default!;
    public string NameEn { get; set; } = default!;
    public string NameAr { get; set; } = default!;
    public string? Description { get; set; }
    public string Category { get; set; } = default!;
    public CatalogStatus Status { get; set; } = CatalogStatus.Active;
    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>An effective-dated, immutable-once-active snapshot of a plan's benefit configuration. Everything
/// downstream — eligibility, authorization, claims — resolves the version in force on the SERVICE DATE
/// (design 38 §7.1), so this type is what makes retrospective adjudication correct.</summary>
public sealed class PlanVersion
{
    public Guid PlanVersionId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid PlanId { get; set; }
    public int VersionNo { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    /// <summary>EXCLUSIVE end of the window; null = open-ended. A successor starts on exactly this date.</summary>
    public DateOnly? EffectiveTo { get; set; }
    public PlanVersionStatus Status { get; set; } = PlanVersionStatus.Draft;
    public Guid? ActivatedBy { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public Guid? SupersededByVersionId { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    public List<BenefitRule> Rules { get; set; } = [];

    /// <summary>True when this version's benefit configuration may still be edited.</summary>
    public bool IsEditable => Status == PlanVersionStatus.Draft;

    /// <summary>Half-open containment per design 38 §7.1: <c>[effective_from, effective_to)</c>. The start day is
    /// in force; the end day belongs to the successor.</summary>
    public bool Covers(DateOnly serviceDate) =>
        serviceDate >= EffectiveFrom && (EffectiveTo is null || serviceDate < EffectiveTo.Value);
}

/// <summary>Per-benefit-category configuration inside a plan version. This is the row that a member's
/// coverage + coverage_limit are GENERATED from at enrolment (19.2), which is what makes an entitlement
/// explainable back to a specific version.</summary>
public sealed class BenefitRule
{
    public Guid RuleId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid PlanVersionId { get; set; }
    public Guid BenefitCategoryId { get; set; }
    public bool IsCovered { get; set; } = true;
    public LimitType? LimitType { get; set; }
    public decimal? LimitValue { get; set; }
    public ResetPeriod ResetPeriod { get; set; } = ResetPeriod.None;
    public decimal? CopayFixed { get; set; }
    public decimal? CopayPercent { get; set; }
    public decimal? Deductible { get; set; }
    public int WaitingPeriodDays { get; set; }
    public bool RequiresPreauth { get; set; }
    public decimal? PreauthCostThreshold { get; set; }
    public string? NetworkTier { get; set; }
    /// <summary>Coded exclusions (jsonb array of codes).</summary>
    public string Exclusions { get; set; } = "[]";
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}
