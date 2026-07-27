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
    decimal? CopayFixed,
    decimal? CopayPercent,
    decimal? Deductible,
    int WaitingPeriodDays,
    bool RequiresPreauth,
    decimal? PreauthCostThreshold,
    string? NetworkTier,
    string? Exclusions,
    string? Notes);

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
    string ResetPeriod, decimal? CopayFixed, decimal? CopayPercent, decimal? Deductible,
    int WaitingPeriodDays, bool RequiresPreauth, decimal? PreauthCostThreshold, string? NetworkTier,
    string Exclusions, string? Notes)
{
    public static BenefitRuleView From(BenefitRule r) =>
        new(r.RuleId, r.BenefitCategoryId, r.IsCovered, r.LimitType?.ToString(), r.LimitValue,
            r.ResetPeriod.ToString(), r.CopayFixed, r.CopayPercent, r.Deductible,
            r.WaitingPeriodDays, r.RequiresPreauth, r.PreauthCostThreshold, r.NetworkTier,
            r.Exclusions, r.Notes);
}
