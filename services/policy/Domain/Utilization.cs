namespace Mersal.Policy.Domain;

// Phase 19.4 — utilization (design 38 §4.3). A READ MODEL. Nothing here writes consumed_value: phase 18 owns
// the accumulator, and a report that could move it would be a second writer to the one number the whole
// benefit spine is arbitrated by.
//
// ============================================================================================================
// THE DISTINCTION THIS FILE EXISTS TO KEEP: "consumed" IS NOT "activity in a window".
// ============================================================================================================
// coverage_limit.consumed_value is the AUTHORITATIVE accumulator, and it is RESET at each period boundary
// (LimitReset). policy.benefit_consumption is the append-only ledger of every move ever made, and it is NOT
// reset — the rows survive the reset that zeroed the accumulator.
//
// So summing the ledger over "all time" gives a LARGER number than the accumulator, and the two are both
// correct: one answers "how much of this year's entitlement is gone", the other answers "how much care did
// this member receive". Reporting either under the other's name is how a utilization report ends up telling
// Finance a member is over their limit when they are not, or under it when they are.
//
// Therefore, and enforced by naming everywhere below:
//   * limit / consumed / remaining / percentUsed / resetsOn  ← the ACCUMULATOR, always the current period.
//   * activity (quantity, events, tier split)                ← the LEDGER, always window-scoped.
// The reconciliation test asserts the first against coverage_limit directly. It can, because the first IS
// coverage_limit — reconciliation is structural here, not a periodic job that hopes two stores still agree.

/// <summary>
/// The accumulator side of one benefit category for one member: what was agreed, what is gone, what is left.
///
/// <para><see cref="PercentUsed"/> is nullable on purpose. A category with no accumulating limit is UNLIMITED,
/// and rendering that as 0% or 100% both lie — 0% invites "plenty left" on something that was never metered,
/// 100% flags an outlier that does not exist. Null renders as "—".</para>
/// </summary>
public sealed record CategoryAccumulator(
    string BenefitCategoryCode,
    Guid CoverageId,
    Guid? CoverageLimitId,
    LimitType? LimitType,
    decimal? LimitValue,
    decimal ConsumedValue,
    string CurrencyCode,
    ResetPeriod ResetPeriod,
    DateOnly? LastResetOn,
    DateOnly? ResetsOn)
{
    /// <summary>Remaining entitlement, floored at zero. A limit REDUCED mid-period can legitimately leave
    /// consumed &gt; limit (0003 keeps the accumulator truthful rather than rejecting care that happened), and
    /// a negative "remaining" on a screen reads as a data fault rather than as an over-consumed benefit.</summary>
    public decimal? Remaining => LimitValue is null ? null : Math.Max(0m, LimitValue.Value - ConsumedValue);

    /// <summary>Null when unlimited. Uncapped above 100 — being 140% of a reduced limit is exactly the fact a
    /// utilization report is for.</summary>
    public decimal? PercentUsed => LimitValue is null or 0m
        ? null
        : Math.Round(ConsumedValue / LimitValue.Value * 100m, 1, MidpointRounding.AwayFromZero);

    public bool IsUnlimited => LimitValue is null;
}

/// <summary>Window-scoped movement, from the ledger. Distinct type from <see cref="CategoryAccumulator"/> so
/// the two can never be added together by accident.</summary>
public sealed record CategoryActivity(
    string BenefitCategoryCode,
    decimal QuantityApplied,
    decimal QuantityReversed,
    int EventCount)
{
    /// <summary>Net movement in the window. Applied minus reversed, because a voided fulfillment did not
    /// happen and counting it would inflate every report a void appears in.</summary>
    public decimal NetQuantity => QuantityApplied - QuantityReversed;
}

/// <summary>
/// Consumption attributed to one network tier (19.1b), which is the lever the Network Team and Finance
/// actually pull: steering volume from out-of-network to a contracted tier is the single largest cost move
/// available to them, and they cannot make it without seeing where the volume currently is.
/// </summary>
public sealed record TierUtilization(
    string TierCode,
    bool IsOutOfNetwork,
    bool IsAttributed,
    decimal NetQuantity,
    int EventCount)
{
    /// <summary>The bucket for movements whose provider is unknown, so no tier can be resolved.</summary>
    public const string UnattributedCode = "UNATTRIBUTED";

    /// <summary>
    /// Unknown attribution is its OWN bucket and is never folded into in-network.
    ///
    /// Folding it in would understate out-of-network — biasing the error in the exact direction that makes
    /// the network look better than it is, on the exact number the network exists to be judged by. An
    /// explicitly unattributed slice is a visible gap someone can close; a silently in-network one is a wrong
    /// answer nobody can see.
    /// </summary>
    public static TierUtilization Unattributed(decimal netQuantity, int events) =>
        new(UnattributedCode, IsOutOfNetwork: false, IsAttributed: false, netQuantity, events);
}

/// <summary>One member's row in a group/plan/policy/payer table.</summary>
public sealed record MemberUtilization(
    Guid EnrollmentId,
    Guid BeneficiaryId,
    string MemberNo,
    Guid PolicyPlanId,
    Guid? GroupId,
    decimal TotalLimit,
    decimal TotalConsumed,
    bool AnyUnlimited)
{
    public decimal TotalRemaining => Math.Max(0m, TotalLimit - TotalConsumed);

    /// <summary>Null when the member holds ANY unlimited category: a percentage computed over a partial
    /// denominator is not a smaller truth, it is a different number wearing the same label.</summary>
    public decimal? PercentUsed => AnyUnlimited || TotalLimit <= 0m
        ? null
        : Math.Round(TotalConsumed / TotalLimit * 100m, 1, MidpointRounding.AwayFromZero);
}

/// <summary>A slice of the member distribution — how many members sit in each consumption band.</summary>
public sealed record DistributionBucket(string Label, decimal FromPercent, decimal? ToPercent, int MemberCount);

/// <summary>Cross-service facts a utilization report needs but policy-service does not own.</summary>
public sealed record ExternalUtilization(
    int? EncounterCount,
    int? AuthorizationsRaised,
    int? AuthorizationsApproved,
    int? AuthorizationsDenied,
    decimal? ClaimedAmount,
    decimal? ApprovedAmount,
    decimal? MemberShareAmount,
    string CurrencyCode = "EGP")
{
    /// <summary>
    /// Every figure null, meaning "we could not ask" — NOT zero.
    ///
    /// A zero here is indistinguishable from "this member used nothing", and those two lead to opposite
    /// decisions: one is a healthy member, the other is a broken report. Nulls render as "unavailable" and
    /// the response says which source failed.
    /// </summary>
    public static readonly ExternalUtilization Unavailable = new(null, null, null, null, null, null, null);

    public bool IsComplete =>
        EncounterCount is not null && AuthorizationsRaised is not null && ClaimedAmount is not null;
}

/// <summary>Pure utilization arithmetic. No I/O, so the endpoint and the tests exercise the same decisions.</summary>
public static class UtilizationMath
{
    /// <summary>Default outlier threshold: members at or above 80% of their entitlement. Configurable per
    /// request because the useful threshold differs by programme — a chronic-care cohort at 80% in June is
    /// normal, a general cohort at 80% in February is not.</summary>
    public const decimal DefaultOutlierThresholdPercent = 80m;

    /// <summary>
    /// The date this limit's accumulator next returns to zero, or null when it never does.
    ///
    /// Derived from the reset period rather than stored, so it cannot go stale between reset runs — a stored
    /// "next reset" is wrong for the whole window between the boundary passing and the job running, which is
    /// precisely when someone is looking at it wondering why their balance has not come back.
    /// </summary>
    public static DateOnly? NextResetOn(ResetPeriod period, LimitType limitType, DateOnly on)
    {
        if (period == ResetPeriod.None || limitType == LimitType.Lifetime) return null;
        var start = LimitReset.PeriodStart(period, on);
        if (start is null) return null;
        return period switch
        {
            ResetPeriod.Monthly => start.Value.AddMonths(1),
            ResetPeriod.Quarterly => start.Value.AddMonths(3),
            ResetPeriod.Yearly => start.Value.AddYears(1),
            _ => null,
        };
    }

    /// <summary>Members at or above the threshold. Unlimited members are excluded rather than treated as 0%:
    /// they have no percentage, so they are neither inside nor outside a percentage band.</summary>
    public static IReadOnlyList<MemberUtilization> Outliers(
        IEnumerable<MemberUtilization> members, decimal thresholdPercent)
    {
        ArgumentNullException.ThrowIfNull(members);
        return [.. members
            .Where(m => m.PercentUsed is { } p && p >= thresholdPercent)
            .OrderByDescending(m => m.PercentUsed)
            .ThenBy(m => m.MemberNo, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The consumption distribution in fixed bands. Members with no percentage (unlimited) land in their own
    /// bucket instead of distorting the first band, where they would read as "barely using their benefit".
    /// </summary>
    public static IReadOnlyList<DistributionBucket> Distribution(IEnumerable<MemberUtilization> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var list = members as IReadOnlyCollection<MemberUtilization> ?? [.. members];

        int Count(decimal from, decimal? to) => list.Count(m =>
            m.PercentUsed is { } p && p >= from && (to is null || p < to.Value));

        return
        [
            new("0–25%", 0m, 25m, Count(0m, 25m)),
            new("25–50%", 25m, 50m, Count(25m, 50m)),
            new("50–75%", 50m, 75m, Count(50m, 75m)),
            new("75–100%", 75m, 100m, Count(75m, 100m)),
            new("100%+", 100m, null, Count(100m, null)),
            new("Unlimited", 0m, null, list.Count(m => m.PercentUsed is null)),
        ];
    }

    /// <summary>
    /// Roll per-member figures into a scope total.
    ///
    /// Summation is over the SAME accumulator rows the member view reports, which is what makes the
    /// group/policy/payer totals reconcile to coverage_limit exactly rather than approximately. Nothing is
    /// re-derived on the way up: an aggregate that recomputes its parts is an aggregate that can disagree
    /// with them.
    /// </summary>
    public static (decimal TotalLimit, decimal TotalConsumed, decimal TotalRemaining, decimal? PercentUsed)
        Roll(IEnumerable<MemberUtilization> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var list = members as IReadOnlyCollection<MemberUtilization> ?? [.. members];
        var limit = list.Sum(m => m.TotalLimit);
        var consumed = list.Sum(m => m.TotalConsumed);
        var remaining = Math.Max(0m, limit - consumed);
        decimal? percent = limit <= 0m ? null : Math.Round(consumed / limit * 100m, 1, MidpointRounding.AwayFromZero);
        return (limit, consumed, remaining, percent);
    }

    /// <summary>Fold ledger movements into per-tier buckets, keeping the unattributed slice separate.</summary>
    public static IReadOnlyList<TierUtilization> SplitByTier(
        IEnumerable<(string? TierCode, bool IsOutOfNetwork, decimal NetQuantity)> movements)
    {
        ArgumentNullException.ThrowIfNull(movements);
        var buckets = new Dictionary<string, (bool Oon, bool Attributed, decimal Qty, int Events)>(StringComparer.Ordinal);

        foreach (var (tierCode, oon, qty) in movements)
        {
            var attributed = !string.IsNullOrWhiteSpace(tierCode);
            var key = attributed ? tierCode! : TierUtilization.UnattributedCode;
            var existing = buckets.TryGetValue(key, out var b)
                ? b
                : (Oon: oon, Attributed: attributed, Qty: 0m, Events: 0);
            buckets[key] = (existing.Oon || oon, attributed, existing.Qty + qty, existing.Events + 1);
        }

        // Ordinal ordering so two runs of the same report produce the same rows in the same order — a table
        // that reshuffles between refreshes is one nobody trusts enough to act on.
        return [.. buckets
            .Select(kv => new TierUtilization(kv.Key, kv.Value.Oon, kv.Value.Attributed, kv.Value.Qty, kv.Value.Events))
            .OrderBy(t => t.IsAttributed ? 0 : 1)
            .ThenBy(t => t.TierCode, StringComparer.Ordinal)];
    }
}
