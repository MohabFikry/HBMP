namespace Mersal.Prescribing;

/// <summary>
/// 29.5 — turns an allocation into DATED windows (design 45 §5).
///
/// <para>Separate from <see cref="ChronicAllocation"/> because they answer different questions and fail
/// differently: the allocation is arithmetic that must sum exactly, and this is a calendar that must not
/// leave gaps. Keeping them apart means the sum invariant is testable without a date in sight.</para>
/// </summary>
public static class WindowSchedule
{
    /// <summary>
    /// Build the dated schedule.
    /// </summary>
    /// <param name="allocation">Per-window quantities, already summing to the rounded total.</param>
    /// <param name="start">The script's first day — normally the prescribing date.</param>
    /// <param name="toleranceDays">How many days early a window will be accepted (default 5).</param>
    public static IReadOnlyList<RefillWindow> Build(
        IReadOnlyList<decimal> allocation, DateOnly start, int frequencyMonths, int durationDays, int toleranceDays)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequencyMonths);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationDays);
        ArgumentOutOfRangeException.ThrowIfNegative(toleranceDays);

        var period = frequencyMonths * ChronicAllocation.DaysPerMonth;
        var scriptEnd = start.AddDays(durationDays - 1);
        var windows = new List<RefillWindow>(allocation.Count);

        for (var i = 0; i < allocation.Count; i++)
        {
            var scheduled = start.AddDays(i * period);

            // The last window closes with the SCRIPT, not a period after its own opening: a script whose
            // duration is not a whole number of periods would otherwise let the final collection happen after
            // the prescription itself had expired.
            var nextScheduled = start.AddDays((i + 1) * period);
            var closes = i == allocation.Count - 1 ? scriptEnd : nextScheduled.AddDays(-1);
            if (closes > scriptEnd) closes = scriptEnd;

            // FIXED windows with an EARLY TOLERANCE — the tolerance moves opens_at and never the scheduled
            // date. Moving the scheduled date would shift the next window too, and the whole point of a fixed
            // schedule is that collecting early does not pull the rest of the script forward with it.
            //
            // Window 1 gets no tolerance: applying it would put opens_at before the script existed, which is
            // harmless at the counter and nonsense in a report.
            var opensAt = i == 0 ? scheduled : scheduled.AddDays(-toleranceDays);

            windows.Add(new RefillWindow(
                WindowNo: i + 1,
                ScheduledOpen: scheduled,
                OpensAt: opensAt,
                ClosesAt: closes,
                AllocatedQuantity: allocation[i],
                DispensedQuantity: 0m,
                Status: WindowStatus.Pending));
        }

        return windows;
    }
}
