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
/// explainable back to a specific version.
///
/// <para>19.1b moved COST SHARE off this type onto <see cref="BenefitRuleTier"/>. What is left here are the
/// properties of the benefit itself — whether it is covered, how much of it, how long the member waits, what
/// is excluded. What the member PAYS depends on where the care was delivered, so it belongs per tier.</para>
/// </summary>
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
    public decimal? Deductible { get; set; }

    /// <summary>The plan's deductible does not apply to this category (primary care commonly waives it).
    /// Deliberately NOT modelled as a zero deductible: "this category is exempt" and "this plan has no
    /// deductible" survive a plan amendment differently, and only the exemption should follow the category.</summary>
    public bool DeductibleWaived { get; set; }

    public int WaitingPeriodDays { get; set; }
    /// <summary>The plan-level default. A tier may override it via
    /// <see cref="BenefitRuleTier.RequiresPreauthOverride"/> — out-of-network care commonly needs
    /// authorization for a service that is open-access in-network.</summary>
    public bool RequiresPreauth { get; set; }
    public decimal? PreauthCostThreshold { get; set; }
    /// <summary>Coded exclusions (jsonb array of codes).</summary>
    public string Exclusions { get; set; } = "[]";
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    /// <summary>The cost-share grid: one row per network tier. Activation requires every Active tier to be
    /// present (19.1b) — an unconfigured tier is a validation error, never a silent default.</summary>
    public List<BenefitRuleTier> Tiers { get; set; } = [];
}

/// <summary>
/// What a member pays for one benefit category AT ONE NETWORK TIER (design 38 §3, phase 19.1b).
///
/// This is what makes "in-network 10%, out-of-network 40% or not covered" expressible. The tier itself is
/// owned by provider-service (network administration); policy administration only decides the price at it —
/// which is why <see cref="NetworkTierId"/> is a plain value rather than a foreign key.
/// </summary>
public sealed class BenefitRuleTier
{
    public Guid RuleTierId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid BenefitRuleId { get; set; }

    /// <summary>provider.network_tier — a cross-service VALUE, validated at write time (no cross-schema FK).</summary>
    public Guid NetworkTierId { get; set; }

    /// <summary>The tier's code, snapshotted at authoring time. A plan version is immutable and stays
    /// resolvable for as long as a claim can reference it, so reading a years-old version must not depend on a
    /// live call into another service.</summary>
    public string TierCode { get; set; } = default!;

    /// <summary>An explicit "not covered at this tier" — a real statement (an HMO paying nothing
    /// out-of-network), and deliberately NOT the same as the tier being absent, which activation rejects.</summary>
    public bool IsCovered { get; set; } = true;

    public decimal? CopayFixed { get; set; }
    public decimal? CopayPercent { get; set; }
    public decimal? CoinsurancePercent { get; set; }

    /// <summary>The co-pay paid here accrues toward the member's deductible for LATER services. It does not
    /// change what they pay today; it changes what they pay next, which is why it is explicit rather than
    /// assumed. The accumulator that consumes it arrives with member-level accumulators (19.2).</summary>
    public bool CopayCountsTowardDeductible { get; set; }

    /// <summary>Overrides <see cref="BenefitRule.RequiresPreauth"/> for this tier; null = inherit.</summary>
    public bool? RequiresPreauthOverride { get; set; }

    /// <summary>Scales the rule's limit at this tier (0.5 = half the ceiling out-of-network); null = inherit.</summary>
    public decimal? LimitMultiplier { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    /// <summary>Whether pre-authorization is required here, resolving the override against the rule default.</summary>
    public bool ResolvesPreauth(BenefitRule rule) =>
        RequiresPreauthOverride ?? (rule ?? throw new ArgumentNullException(nameof(rule))).RequiresPreauth;

    /// <summary>The limit that applies at this tier, resolving the multiplier against the rule's own limit.
    /// Null stays null — an unlimited benefit is not made finite by a tier multiplier.</summary>
    public decimal? ResolvesLimit(BenefitRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.LimitValue is not { } limit) return null;
        // BANKER'S, matching Mersal.Money. This is an amount in EGP at the platform's 2dp settlement scale,
        // and it used to round half AWAY FROM ZERO — so a tier limit landing on a half-piastre came out a
        // piastre higher here than the same figure does anywhere claims or eligibility computes it. See the
        // rule in libs/money/Tests: at Money.Scale there is one rounding mode.
        return LimitMultiplier is { } m ? decimal.Round(limit * m, 2, MidpointRounding.ToEven) : limit;
    }
}

/// <summary>A tier as policy administration needs to know it: an id and a code. Deliberately NOT a copy of
/// provider-service's entity — policy-service consumes the catalogue, it does not model the network.</summary>
public sealed record NetworkTierRef(Guid NetworkTierId, string TierCode);
