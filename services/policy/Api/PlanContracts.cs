using Mersal.Policy.Domain;

namespace Mersal.Policy.Api;

// Phase 19.1 request/response contracts for the PAS product layer (design 38 §3, §4.1).

// CreatePayer / PayerView and the rest of the payer surface live in PayerContracts.cs (19.7).

public sealed record CreatePlan(string PlanCode, string NameEn, string NameAr, string? Description, string Category);

public sealed record CreatePlanVersion(Guid PlanId, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

/// <summary>The whole benefit configuration of a draft, replaced as a unit.</summary>
public sealed record SetBenefitRules(BenefitRuleInput[] Rules);

public sealed record BenefitRuleInput(
    string BenefitCategoryCode,
    bool IsCovered,
    string? LimitType,
    decimal? LimitValue,
    string? ResetPeriod,
    decimal? Deductible,
    /// <summary>The plan's deductible does not apply to this category (primary care commonly waives it).</summary>
    bool DeductibleWaived,
    int WaitingPeriodDays,
    bool RequiresPreauth,
    decimal? PreauthCostThreshold,
    string? Exclusions,
    string? Notes,
    /// <summary>19.1b — the cost-share grid, one entry per Active network tier. Activation rejects a covered
    /// category that leaves any Active tier unpriced, so this is not optional in practice.</summary>
    BenefitRuleTierInput[]? Tiers);

/// <summary>What the member pays for this category at one tier. <c>networkTierId</c> refers to a tier owned by
/// provider-service; policy administration prices tiers but does not create them (19.1b).</summary>
public sealed record BenefitRuleTierInput(
    Guid NetworkTierId,
    bool IsCovered,
    decimal? CopayFixed,
    decimal? CopayPercent,
    decimal? CoinsurancePercent,
    /// <summary>The co-pay paid here accrues toward the member's deductible for LATER services.</summary>
    bool CopayCountsTowardDeductible,
    bool? RequiresPreauthOverride,
    decimal? LimitMultiplier);

/// <summary>19.6 — reference data for the plan-version editor's row set. Codes and names only.</summary>
public sealed record BenefitCategoryView(Guid BenefitCategoryId, string Code, string Name);

public sealed record PlanView(Guid PlanId, string PlanCode, string NameEn, string NameAr, string? Description, string Category, string Status)
{
    public static PlanView From(Plan p) =>
        new(p.PlanId, p.PlanCode, p.NameEn, p.NameAr, p.Description, p.Category, p.Status.ToString());
}

/// <summary><c>Editable</c> is projected rather than left for the client to infer from the status: the UI's
/// read-only affordance and the API's 409 must agree, and deriving the rule twice is how they drift apart.</summary>
public sealed record PlanVersionView(
    Guid PlanVersionId, Guid PlanId, int VersionNo, DateOnly EffectiveFrom, DateOnly? EffectiveTo,
    string Status, bool Editable, DateTimeOffset? ActivatedAt, Guid? SupersededByVersionId,
    IReadOnlyList<BenefitRuleView> Rules)
{
    /// <param name="categoryCodes">benefit-category id → code. 19.6: without it the response identifies each
    /// rule's category by an id while <see cref="BenefitRuleInput"/> writes it back by CODE, so a client could
    /// read a draft and could not re-submit it. Optional so a caller with no catalogue to hand still gets the
    /// version; the code is then null rather than guessed.</param>
    public static PlanVersionView From(PlanVersion v, IReadOnlyDictionary<Guid, string>? categoryCodes = null)
    {
        ArgumentNullException.ThrowIfNull(v);
        return new(v.PlanVersionId, v.PlanId, v.VersionNo, v.EffectiveFrom, v.EffectiveTo,
            v.Status.ToString(), v.IsEditable, v.ActivatedAt, v.SupersededByVersionId,
            [.. v.Rules.Select(r => BenefitRuleView.From(r, categoryCodes))]);
    }
}

public sealed record BenefitRuleView(
    Guid RuleId, Guid BenefitCategoryId, string? BenefitCategoryCode, bool IsCovered, string? LimitType,
    decimal? LimitValue,
    string ResetPeriod, decimal? Deductible, bool DeductibleWaived, int WaitingPeriodDays, bool RequiresPreauth,
    decimal? PreauthCostThreshold, string Exclusions, string? Notes,
    IReadOnlyList<BenefitRuleTierView> Tiers)
{
    public static BenefitRuleView From(BenefitRule r, IReadOnlyDictionary<Guid, string>? categoryCodes = null)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new(r.RuleId, r.BenefitCategoryId,
            categoryCodes is not null && categoryCodes.TryGetValue(r.BenefitCategoryId, out var code) ? code : null,
            r.IsCovered, r.LimitType?.ToString(), r.LimitValue,
            r.ResetPeriod.ToString(), r.Deductible, r.DeductibleWaived, r.WaitingPeriodDays, r.RequiresPreauth,
            r.PreauthCostThreshold, r.Exclusions, r.Notes,
            [.. r.Tiers.OrderBy(t => t.TierCode, StringComparer.Ordinal).Select(t => BenefitRuleTierView.From(t, r))]);
    }
}

/// <summary><c>effectiveRequiresPreauth</c> and <c>effectiveLimitValue</c> are projected rather than left for
/// the client to compute from the override and the multiplier: eligibility, approvals, claims and the UI must
/// all agree on what applies at this tier, and resolving it four times is how they come to disagree.</summary>
public sealed record BenefitRuleTierView(
    Guid RuleTierId, Guid NetworkTierId, string TierCode, bool IsCovered,
    decimal? CopayFixed, decimal? CopayPercent, decimal? CoinsurancePercent,
    bool CopayCountsTowardDeductible, bool? RequiresPreauthOverride, decimal? LimitMultiplier,
    bool EffectiveRequiresPreauth, decimal? EffectiveLimitValue)
{
    public static BenefitRuleTierView From(BenefitRuleTier t, BenefitRule rule)
    {
        ArgumentNullException.ThrowIfNull(t);
        return new(t.RuleTierId, t.NetworkTierId, t.TierCode, t.IsCovered,
            t.CopayFixed, t.CopayPercent, t.CoinsurancePercent,
            t.CopayCountsTowardDeductible, t.RequiresPreauthOverride, t.LimitMultiplier,
            t.ResolvesPreauth(rule), t.ResolvesLimit(rule));
    }
}

/// <summary>19.1b — the authored cost share for one (plan version, benefit category, network tier). The shape
/// <c>libs/benefit-pricing</c> reads, and the ONLY thing policy-service publishes about pricing: it states what
/// was AGREED and performs no arithmetic, so the split can happen in exactly one place.</summary>
public sealed record CostShareView(
    Guid NetworkTierId, string TierCode, bool IsCovered,
    decimal? CopayFixed, decimal? CopayPercent, decimal? CoinsurancePercent,
    decimal? Deductible, bool DeductibleWaived, bool CopayCountsTowardDeductible,
    bool RequiresPreauth, decimal? LimitValue);
