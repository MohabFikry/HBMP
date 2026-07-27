using Mersal.Policy.Domain;

namespace Mersal.Policy.Api;

// Phase 19.1 request/response contracts for the PAS product layer (design 38 §3, §4.1).

public sealed record CreatePayer(string PayerCode, string NameEn, string NameAr, string PayerType, string? Contact);

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
    bool? RequiresPreauthOverride,
    decimal? LimitMultiplier);

public sealed record PayerView(Guid PayerId, string PayerCode, string NameEn, string NameAr, string PayerType, string Status)
{
    public static PayerView From(Payer p) =>
        new(p.PayerId, p.PayerCode, p.NameEn, p.NameAr, p.PayerType.ToString(), p.Status.ToString());
}

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
    public static PlanVersionView From(PlanVersion v) =>
        new(v.PlanVersionId, v.PlanId, v.VersionNo, v.EffectiveFrom, v.EffectiveTo,
            v.Status.ToString(), v.IsEditable, v.ActivatedAt, v.SupersededByVersionId,
            [.. v.Rules.Select(BenefitRuleView.From)]);
}

public sealed record BenefitRuleView(
    Guid RuleId, Guid BenefitCategoryId, bool IsCovered, string? LimitType, decimal? LimitValue,
    string ResetPeriod, decimal? Deductible, int WaitingPeriodDays, bool RequiresPreauth,
    decimal? PreauthCostThreshold, string Exclusions, string? Notes,
    IReadOnlyList<BenefitRuleTierView> Tiers)
{
    public static BenefitRuleView From(BenefitRule r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new(r.RuleId, r.BenefitCategoryId, r.IsCovered, r.LimitType?.ToString(), r.LimitValue,
            r.ResetPeriod.ToString(), r.Deductible, r.WaitingPeriodDays, r.RequiresPreauth,
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
    bool? RequiresPreauthOverride, decimal? LimitMultiplier,
    bool EffectiveRequiresPreauth, decimal? EffectiveLimitValue)
{
    public static BenefitRuleTierView From(BenefitRuleTier t, BenefitRule rule)
    {
        ArgumentNullException.ThrowIfNull(t);
        return new(t.RuleTierId, t.NetworkTierId, t.TierCode, t.IsCovered,
            t.CopayFixed, t.CopayPercent, t.CoinsurancePercent,
            t.RequiresPreauthOverride, t.LimitMultiplier,
            t.ResolvesPreauth(rule), t.ResolvesLimit(rule));
    }
}
