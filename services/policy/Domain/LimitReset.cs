namespace Mersal.Policy.Domain;

/// <summary>
/// Reset math for coverage limits (1.2): limits with a reset_period roll their consumed_value back to
/// 0 when the period boundary passes; Lifetime and None never reset. Every reset writes a _history row
/// + audit event (handled in the app/job); this class is the pure decision + application.
/// </summary>
public static class LimitReset
{
    /// <summary>The start date of the reset window containing <paramref name="on"/> for a period.</summary>
    public static DateOnly? PeriodStart(ResetPeriod period, DateOnly on) => period switch
    {
        ResetPeriod.Monthly => new DateOnly(on.Year, on.Month, 1),
        ResetPeriod.Quarterly => new DateOnly(on.Year, ((on.Month - 1) / 3) * 3 + 1, 1),
        ResetPeriod.Yearly => new DateOnly(on.Year, 1, 1),
        _ => null, // None / Lifetime handled by ResetPeriod.None; Lifetime limits use None
    };

    /// <summary>
    /// True when a reset is due: the current period's start is strictly later than the last reset.
    ///
    /// 18.A3 (audit R2 X10): this used to treat <c>LastResetOn is null</c> as "due as soon as anything
    /// has been consumed", which WIPED in-period consumption the first time the job ran — a member who
    /// had used 8 of their 10 annual visits was silently handed all 10 back. A resettable limit is now
    /// seeded with the period start containing its coverage's effective date
    /// (<see cref="SeedLastResetOn"/>), so "never reset" is not a state a live limit can be in, and a
    /// null here is conservatively treated as NOT due rather than as a licence to zero the accumulator.
    /// </summary>
    public static bool IsResetDue(CoverageLimit limit, DateOnly now)
    {
        ArgumentNullException.ThrowIfNull(limit);
        if (limit.ResetPeriod == ResetPeriod.None || limit.LimitType == LimitType.Lifetime) return false;
        var currentStart = PeriodStart(limit.ResetPeriod, now);
        if (currentStart is null || limit.LastResetOn is null) return false;
        return currentStart > limit.LastResetOn;   // moved into a new period
    }

    /// <summary>The <c>last_reset_on</c> a limit is born with: the start of the period containing the
    /// coverage's effective date. Anchoring the accumulator to its own period means the first boundary
    /// crossing after creation is a real reset, and nothing before it is.</summary>
    public static DateOnly? SeedLastResetOn(ResetPeriod period, LimitType limitType, DateOnly coverageEffectiveFrom) =>
        period == ResetPeriod.None || limitType == LimitType.Lifetime
            ? null
            : PeriodStart(period, coverageEffectiveFrom);

    /// <summary>Apply a reset in place (consumed → 0, stamp last_reset). Returns true if it changed.</summary>
    public static bool ApplyIfDue(CoverageLimit limit, DateOnly now)
    {
        if (!IsResetDue(limit, now)) return false;
        limit.ConsumedValue = 0m;
        limit.LastResetOn = PeriodStart(limit.ResetPeriod, now);
        return true;
    }
}
