namespace Mersal.Policy.Domain;

// Phase 19.5 — full coverage details for one member (design 38 §4.5).
//
// ============================================================================================================
// THE RULE SAYS WHAT WAS PROMISED; THE ACCUMULATOR SAYS WHAT IS LEFT. BOTH, SIDE BY SIDE.
// ============================================================================================================
// A benefit rule (19.1) is the CONFIGURATION a member was enrolled against: covered, ceiling, waiting period,
// pre-auth, exclusions, and the per-tier price. A coverage_limit (18) is the member's LIVE BALANCE against it.
//
// They can legitimately disagree. A plan version amended mid-year raises next year's ceiling while this year's
// generated coverage still carries the old one; a limit reduced by endorsement leaves consumed above limit.
// Rendering only the rule would tell a member they have cover they have already spent; rendering only the
// accumulator would leave "why is my ceiling 5 000" unanswerable. So this type carries both, names which is
// which, and flags the disagreement rather than picking a winner.

/// <summary>What a member pays for one category at one network tier — the row of the cost-share grid.</summary>
public sealed record TierCostShare(
    Guid NetworkTierId,
    string TierCode,
    bool IsCovered,
    decimal? CopayFixed,
    decimal? CopayPercent,
    decimal? CoinsurancePercent,
    bool CopayCountsTowardDeductible,
    bool RequiresPreauth,
    decimal? LimitAtTier)
{
    public static TierCostShare From(BenefitRuleTier tier, BenefitRule rule)
    {
        ArgumentNullException.ThrowIfNull(tier);
        ArgumentNullException.ThrowIfNull(rule);
        return new(tier.NetworkTierId, tier.TierCode, tier.IsCovered, tier.CopayFixed, tier.CopayPercent,
            tier.CoinsurancePercent, tier.CopayCountsTowardDeductible, tier.ResolvesPreauth(rule),
            tier.ResolvesLimit(rule));
    }
}

/// <summary>One benefit category, as the member's own coverage card shows it.</summary>
public sealed record CategoryCoverageDetail(
    string BenefitCategoryCode,
    bool IsCovered,
    string? LimitType,
    /// <summary>The ceiling the member's GENERATED coverage carries — what the accumulator is measured against.
    /// Null = unlimited.</summary>
    decimal? Limit,
    decimal Consumed,
    decimal? Remaining,
    decimal? PercentUsed,
    string CurrencyCode,
    string ResetPeriod,
    DateOnly? ResetsOn,
    /// <summary>The ceiling the plan version IN FORCE would grant today. Differs from <see cref="Limit"/> when
    /// the plan was amended after this member was enrolled — a real and legitimate divergence, surfaced rather
    /// than hidden.</summary>
    decimal? ConfiguredLimit,
    bool LimitDiffersFromPlan,
    DateOnly? WaitingPeriodEndsOn,
    string WaitingPeriodState,
    bool RequiresPreauth,
    decimal? PreauthCostThreshold,
    decimal? Deductible,
    bool DeductibleWaived,
    IReadOnlyList<string> Exclusions,
    IReadOnlyList<TierCostShare> CostShareByTier);

/// <summary>A member's coverage as a whole, with the provenance that makes it explainable.</summary>
public sealed record MemberCoverageDetail(
    Guid EnrollmentId,
    Guid BeneficiaryId,
    string MemberNo,
    Guid PolicyId,
    Guid PolicyPlanId,
    string PlanLabel,
    Guid? PlanId,
    /// <summary>The version whose rules are being shown: the one in force on the service date asked about, NOT
    /// "the current version". A claim for February is judged by February's rules (design 38 §7.1).</summary>
    Guid? PlanVersionInForceId,
    int? PlanVersionNo,
    DateOnly? PlanVersionFrom,
    DateOnly? PlanVersionTo,
    string? PlanVersionStatus,
    /// <summary>The version this member's coverage was GENERATED from at enrolment. When it differs from the
    /// version in force, the member is holding entitlements authored under older rules — the single most useful
    /// fact on this page when someone asks why two members of the same plan have different ceilings.</summary>
    Guid? EnrolledUnderPlanVersionId,
    bool PlanVersionChangedSinceEnrolment,
    DateOnly AsOf,
    string EnrollmentStatus,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    IReadOnlyList<CategoryCoverageDetail> Categories,
    IReadOnlyList<CoverageChangeEntry> History);

/// <summary>One effective-dated change to the membership — including plan changes (design 38 §4.5).</summary>
public sealed record CoverageChangeEntry(
    Guid EventId,
    string EventType,
    DateOnly EffectiveDate,
    DateTimeOffset OccurredAt,
    bool IsRetroEffective,
    string? Reason,
    string Payload);

public static class CoverageDetailAssembler
{
    /// <summary>
    /// Assemble one category row from the rule (configuration) and the member's coverage (balance).
    /// </summary>
    /// <param name="rule">The benefit rule from the plan version in force, or null when the version in force no
    /// longer configures this category at all — which is itself worth showing, because the member still holds
    /// generated coverage for it.</param>
    public static CategoryCoverageDetail Category(
        string categoryCode, BenefitRule? rule, Coverage? coverage, DateOnly enrolledFrom, DateOnly asOf)
    {
        var limits = coverage?.Limits.Where(l => BenefitAccumulation.Accumulates(l.LimitType)).ToList() ?? [];
        var limit = limits.Count == 0 ? (decimal?)null : limits.Sum(l => l.LimitValue);
        var consumed = limits.Sum(l => l.ConsumedValue);
        var primary = limits.Count > 0 ? limits[0] : null;

        var configured = rule?.LimitValue;
        var waitingEndsOn = rule is null ? null : WaitingPeriod.EndsOnFor(rule, enrolledFrom);

        return new CategoryCoverageDetail(
            categoryCode,
            // Covered = the member HOLDS coverage, or the plan says so. A member keeps a generated coverage row
            // through a plan amendment that later drops the category, and their balance is still spendable.
            IsCovered: coverage is not null || (rule?.IsCovered ?? false),
            LimitType: primary?.LimitType.ToString() ?? rule?.LimitType?.ToString(),
            Limit: limit,
            Consumed: consumed,
            Remaining: limit is null ? null : Math.Max(0m, limit.Value - consumed),
            PercentUsed: limit is null or 0m
                ? null
                : Math.Round(consumed / limit.Value * 100m, 1, MidpointRounding.AwayFromZero),
            CurrencyCode: primary?.CurrencyCode ?? "EGP",
            ResetPeriod: (primary?.ResetPeriod ?? rule?.ResetPeriod ?? ResetPeriod.None).ToString(),
            ResetsOn: primary is null
                ? null
                : UtilizationMath.NextResetOn(primary.ResetPeriod, primary.LimitType, asOf),
            ConfiguredLimit: configured,
            // Only a real disagreement counts: both known and different. An unlimited member on an unlimited
            // rule is not a divergence, and flagging it would train readers to ignore the flag.
            LimitDiffersFromPlan: limit is { } l && configured is { } c && l != c,
            WaitingPeriodEndsOn: waitingEndsOn,
            WaitingPeriodState: (waitingEndsOn is null ? Domain.WaitingPeriodState.None
                : asOf <= waitingEndsOn.Value ? Domain.WaitingPeriodState.Serving
                : Domain.WaitingPeriodState.Served).ToString(),
            RequiresPreauth: rule?.RequiresPreauth ?? false,
            PreauthCostThreshold: rule?.PreauthCostThreshold,
            Deductible: rule?.Deductible,
            DeductibleWaived: rule?.DeductibleWaived ?? false,
            Exclusions: ParseExclusions(rule?.Exclusions),
            CostShareByTier: rule is null
                ? []
                : [.. rule.Tiers.OrderBy(t => t.TierCode, StringComparer.Ordinal).Select(t => TierCostShare.From(t, rule))]);
    }

    /// <summary>Coded exclusions are stored as a jsonb array. A malformed value yields an EMPTY list and never
    /// throws: an unreadable exclusion list must not take down the page that shows a member what they are
    /// entitled to — but it is also never rendered as "no exclusions" silently, because the caller sees the raw
    /// count alongside.</summary>
    public static IReadOnlyList<string> ParseExclusions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
            return parsed is null ? [] : [.. parsed];
        }
        catch (System.Text.Json.JsonException) { return []; }
    }
}
