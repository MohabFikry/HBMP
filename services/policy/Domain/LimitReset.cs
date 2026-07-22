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
    /// True when a reset is due: the current period's start is later than the last reset (or there has
    /// never been one and the limit has been consumed within a resettable period).
    /// </summary>
    public static bool IsResetDue(CoverageLimit limit, DateOnly now)
    {
        if (limit.ResetPeriod == ResetPeriod.None || limit.LimitType == LimitType.Lifetime) return false;
        var currentStart = PeriodStart(limit.ResetPeriod, now);
        if (currentStart is null) return false;
        return limit.LastResetOn is null
            ? limit.ConsumedValue > 0            // never reset but has usage → due at first boundary check
            : currentStart > limit.LastResetOn;  // moved into a new period
    }

    /// <summary>Apply a reset in place (consumed → 0, stamp last_reset). Returns true if it changed.</summary>
    public static bool ApplyIfDue(CoverageLimit limit, DateOnly now)
    {
        if (!IsResetDue(limit, now)) return false;
        limit.ConsumedValue = 0m;
        limit.LastResetOn = PeriodStart(limit.ResetPeriod, now);
        return true;
    }
}
