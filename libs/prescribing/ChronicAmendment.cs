namespace Mersal.Prescribing;

/// <summary>What a re-allocation attempt concluded.</summary>
public enum AmendmentOutcome
{
    /// <summary>The remainder was re-allocated across the remaining windows and sums exactly.</summary>
    Reallocated,

    /// <summary>The new total is below what has already been handed over. Invariant 4 — it implies
    /// un-dispensing, and the alternative is a record claiming the patient returned medicine.</summary>
    BelowDispensed,

    /// <summary>The new duration is one month or less, so the script no longer meets the chronic definition
    /// (design 45 §5). NOT a refusal and NOT a silent conversion — the prescriber is asked.</summary>
    NoLongerChronic,

    /// <summary>The prescriber confirmed the conversion. The script becomes acute and carries no refill
    /// schedule at all.</summary>
    ConvertedToAcute,

    /// <summary>The drug master does not say whether the pack may be split, so no quantity can be computed.
    /// <see cref="ChronicReallocation.MissingField"/> names the missing fact.</summary>
    NotChecked,
}

/// <param name="NewTotal">The total for the AMENDED duration, rounded once at the total.</param>
/// <param name="AlreadyDispensed">What the collected windows actually handed over. Read, never recomputed.</param>
/// <param name="RemainingWindows">The re-allocated remainder. EMPTY on any non-<see cref="AmendmentOutcome.Reallocated"/>
/// outcome, and empty is also a legitimate result when the remainder is genuinely zero — a list of zeroes
/// would read as real allocations of nothing.</param>
public sealed record ChronicReallocation(
    AmendmentOutcome Outcome,
    decimal NewTotal,
    decimal AlreadyDispensed,
    IReadOnlyList<decimal> RemainingWindows,
    AllocationUnit Unit = AllocationUnit.PrescribingUnits,
    string? MissingField = null);

/// <summary>
/// 30.3 — re-allocating a chronic script that has already started (design 46 §4).
///
/// <para><b>What was dispensed is a fact and is never recalculated.</b> That single sentence decides the
/// shape: the amendment recomputes the TOTAL from the new duration, subtracts what was actually handed over,
/// and splits only what is left. The sum invariant becomes
/// <c>alreadyDispensed + Σ(remaining) == newTotal</c>, exactly — and it is the property that breaks silently,
/// because every individual window looks like a sensible number and only the sum is wrong.</para>
///
/// <para><b>It computes no rounding of its own.</b> The total comes from <see cref="ChronicAllocation.Plan"/>
/// and the split from <see cref="ChronicAllocation.Split"/>, unchanged. A second rounding implementation here
/// is exactly how "round once, at the total" would stop being true on the amendment path while remaining true
/// on the prescribing path — and the divergence would surface as a patient over-supplied after a correction.</para>
///
/// <para>Pure arithmetic. Dates are <see cref="WindowSchedule"/>'s job and the anchor is the caller's; keeping
/// them apart means the sum invariant stays testable without a calendar in sight.</para>
/// </summary>
public static class ChronicAmendment
{
    /// <summary>
    /// Re-allocate a chronic script to a new duration and/or frequency.
    /// </summary>
    /// <param name="amended">The line as amended — same dose and times per day, new duration and frequency.</param>
    /// <param name="alreadyDispensed">The sum the collected windows actually handed over.</param>
    /// <param name="windowsAlreadyStarted">How many windows have been collected, so the remaining count can
    /// be taken from the tail of the new schedule rather than from its start.</param>
    /// <param name="convertToAcute">The prescriber's EXPLICIT confirmation that a script shortened to one
    /// month or less should become acute. Absent, that case is reported rather than decided.</param>
    public static ChronicReallocation Reallocate(
        AllocationRequest amended, decimal alreadyDispensed, int windowsAlreadyStarted,
        bool convertToAcute = false)
    {
        ArgumentNullException.ThrowIfNull(amended);
        ArgumentOutOfRangeException.ThrowIfNegative(alreadyDispensed);
        ArgumentOutOfRangeException.ThrowIfNegative(windowsAlreadyStarted);

        // The total for the new duration, via the SAME planner the original script used. Its NotChecked path
        // carries through unchanged: an amendment must not be the route by which a missing
        // is_pack_splittable quietly becomes an assumed one.
        var plan = ChronicAllocation.Plan(amended);
        if (plan.NotChecked)
            return new ChronicReallocation(AmendmentOutcome.NotChecked, 0, alreadyDispensed, [],
                plan.Unit, plan.MissingField);

        // Checked BEFORE the chronic-definition question, and that order is deliberate. A prescriber who has
        // asked for something impossible ("give back 30 units") should be told THAT, not offered a conversion
        // to acute that would still be impossible — the confirmation flag authorises the conversion, never
        // un-dispensing.
        if (plan.Total < alreadyDispensed)
            return new ChronicReallocation(AmendmentOutcome.BelowDispensed, plan.Total, alreadyDispensed, [],
                plan.Unit);

        // Design 46 §4: reducing duration to a month or less makes the script no longer chronic, and the
        // system must not silently keep a "chronic" script that is not one. Reported, unless the prescriber
        // has explicitly said to convert.
        if (!ChronicAllocation.IsChronicDuration(amended.DurationDays))
            return new ChronicReallocation(
                convertToAcute ? AmendmentOutcome.ConvertedToAcute : AmendmentOutcome.NoLongerChronic,
                // The total is still reported on the conversion, because an acute script needs a quantity.
                // On the refusal it is reported too — the prescriber is deciding, and "75 units over 25 days"
                // is the fact that decision turns on.
                plan.Total, alreadyDispensed, [], plan.Unit);

        var remaining = plan.Total - alreadyDispensed;

        // How many windows the new schedule still has ahead of it. Clamped at zero: shortening a script below
        // the point already reached leaves nothing further to schedule, and that is a legitimate state rather
        // than an error — the remainder is zero by the check above.
        var remainingWindows = Math.Max(0,
            ChronicAllocation.WindowCount(amended.DurationDays, amended.FrequencyMonths) - windowsAlreadyStarted);

        // An empty list, never a list of zeroes. A zero window is a scheduled collection of nothing: the
        // patient is told to attend and handed nothing, and the sweeper later records a forfeiture of none.
        if (remainingWindows == 0 || remaining <= 0)
            return new ChronicReallocation(AmendmentOutcome.Reallocated, plan.Total, alreadyDispensed, [],
                plan.Unit);

        return new ChronicReallocation(AmendmentOutcome.Reallocated, plan.Total, alreadyDispensed,
            ChronicAllocation.Split(remaining, remainingWindows), plan.Unit);
    }
}
