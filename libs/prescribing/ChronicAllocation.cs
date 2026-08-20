namespace Mersal.Prescribing;

/// <summary>What the allocated quantities are counted in.</summary>
public enum AllocationUnit
{
    /// <summary>Prescribing units — tablets, capsules, mL, sachets. The pack may be broken.</summary>
    PrescribingUnits,

    /// <summary>Whole packs — inhalers, pens, vials, ampoules, patches. The pack may not be broken, so the
    /// total rounds UP to a whole item and the windows are whole items.</summary>
    WholePacks,
}

/// <summary>
/// Everything the allocation needs. Nullable where the drug master may not have said
/// (design 45 §6) — absence is carried through as absence, never defaulted.
/// </summary>
/// <param name="IsPackSplittable">Null ⇒ the drug master does not say. NOT assumed true: assuming splittable
/// is the dangerous default, because it silently permits a fractional inhaler.</param>
/// <param name="PackContent">How many prescribing units one BOX HOLDS. Required only when the pack cannot be
/// split, because that is the only case where the conversion needs it.</param>
public sealed record AllocationRequest(
    decimal DosePerAdministration,
    int TimesPerDay,
    int DurationDays,
    int FrequencyMonths,
    bool? IsPackSplittable,
    decimal? PackContent);

/// <summary>
/// The computed schedule, or a stated refusal to compute one.
/// </summary>
/// <param name="NotChecked">True ⇒ the quantity could NOT be computed, and <see cref="MissingField"/> says
/// which fact was missing. <see cref="Windows"/> is EMPTY, never zeroes: "absence of data is never a clean
/// result", and a zero would read as a real allocation of nothing.</param>
public sealed record AllocationPlan(
    decimal Total,
    AllocationUnit Unit,
    IReadOnlyList<decimal> Windows,
    bool NotChecked = false,
    string? MissingField = null);

/// <summary>
/// 29.5 — the chronic allocation (design 45 §5). Pure arithmetic, no I/O.
///
/// <para><b>Round ONCE, at the total. Never per window.</b> That single sentence is the whole design.
/// Rounding each window independently lets the sum drift ABOVE the prescribed amount — 100 split three ways
/// and rounded per window is 34 + 34 + 34 = 102 — which over-supplies the patient and over-consumes their
/// benefit. It does so silently, because every individual window looks like a sensible number and only the
/// sum is wrong.</para>
///
/// <para>So the order is fixed: compute the total, round the TOTAL to the dispensable unit, then distribute
/// integers that already add up to it. The distribution is largest-remainder — the only step where a choice
/// remains — so no window is more than one unit short of any other and the patient does not run out in the
/// month that got the short end.</para>
/// </summary>
public static class ChronicAllocation
{
    /// <summary>A month, for window purposes. Design 45 §5 uses 30 days explicitly; a calendar month would
    /// make a script's window count depend on which month it started in, which is not something a prescriber
    /// can predict or a pharmacist explain.</summary>
    public const int DaysPerMonth = 30;

    /// <summary>Design 45 §5 — "Chronic requires a duration greater than one month. A 14-day course is not
    /// chronic." Strictly greater: exactly 30 days is one month, not more than one.</summary>
    public static bool IsChronicDuration(int durationDays) => durationDays > DaysPerMonth;

    /// <summary>How many windows a duration divides into at a given refill frequency: ⌈duration ÷ (months ×
    /// 30)⌉. The CEILING, so a 31-day script at monthly frequency is two windows rather than one that quietly
    /// runs a day short.</summary>
    public static int WindowCount(int durationDays, int frequencyMonths)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationDays);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequencyMonths);

        var period = frequencyMonths * DaysPerMonth;
        return (durationDays + period - 1) / period;
    }

    /// <summary>
    /// Distribute <paramref name="total"/> across <paramref name="windows"/> as integers that sum EXACTLY to
    /// it, by largest remainder, highest first.
    /// </summary>
    /// <remarks>
    /// <para>The base is ⌊total ÷ windows⌋ and the remainder is distributed one unit at a time to the first
    /// <c>total mod windows</c> windows. That is largest-remainder for a uniform split, and it gives both
    /// properties design 45 §5 asks for: the sum is exact by construction (base × windows + remainder =
    /// total), and no two windows differ by more than one.</para>
    /// <para><b>Highest first</b> is the stated order and not merely a convenient one: a doctor reviewing the
    /// schedule before submitting sees the larger collection first, and it is also the one already dispensed
    /// if the script is interrupted.</para>
    /// </remarks>
    public static IReadOnlyList<decimal> Split(decimal total, int windows)
    {
        if (windows <= 0)
            throw new ArgumentOutOfRangeException(nameof(windows), windows, "A script must have at least one window.");
        if (total < 0)
            throw new ArgumentOutOfRangeException(nameof(total), total, "A prescribed total cannot be negative.");

        // Integers only. The total has ALREADY been rounded to the dispensable unit by the time it gets here;
        // this step must not introduce a fraction that the counter cannot hand over.
        var units = (long)decimal.Truncate(total);
        var baseQty = units / windows;
        var remainder = (int)(units % windows);

        var result = new decimal[windows];
        for (var i = 0; i < windows; i++) result[i] = baseQty + (i < remainder ? 1 : 0);
        return result;
    }

    /// <summary>
    /// The full plan: total, unit, and the per-window quantities.
    /// </summary>
    public static AllocationPlan Plan(AllocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ---- Missing unit data ⇒ NotChecked, NAMING the field (invariant 8) -------------------------------
        //
        // Checked BEFORE any arithmetic, so there is no partially-computed number to be tempted into
        // returning. "A silently wrong quantity is a dispensing error."
        if (request.IsPackSplittable is null)
            return NotChecked("is_pack_splittable");

        // 31.5 — `pack_content`, not `pack_size`. 31.3 established that the catalogue's pack size counts
        // CONTAINERS for every measured product and replaced the divisor in `QuantityMath`; this path was
        // missed. A 120 ml bottle of syrup is `pack_size = 1`, so a ninety-day course at 10 ml twice a day
        // allocated eighteen hundred "packs" across its windows, and the composer would have shown it.
        if (request.IsPackSplittable == false && request.PackContent is not > 0)
            return NotChecked("pack_content");

        // ---- 1. The total, in prescribing units ----------------------------------------------------------
        var totalUnits = request.DosePerAdministration * request.TimesPerDay * request.DurationDays;

        // ---- 2. Round ONCE, at the total -----------------------------------------------------------------
        decimal total;
        AllocationUnit unit;
        if (request.IsPackSplittable == true)
        {
            // Splittable: the pack can be broken, so the dispensable unit IS the prescribing unit. Rounded up
            // to a whole one — half a tablet is a real thing to prescribe but not a thing a pharmacy counts
            // out across a three-month script.
            total = Math.Ceiling(totalUnits);
            unit = AllocationUnit.PrescribingUnits;
        }
        else
        {
            // Non-splittable: convert to whole packs, rounding UP. A patient who needs 360 puffs and is given
            // one 200-puff canister runs out; the rounding direction is not a matter of taste.
            total = Math.Ceiling(totalUnits / request.PackContent!.Value);
            unit = AllocationUnit.WholePacks;
        }

        // ---- 3 & 4. Windows, then the integer split ------------------------------------------------------
        var windows = WindowCount(request.DurationDays, request.FrequencyMonths);
        return new AllocationPlan(total, unit, Split(total, windows));
    }

    private static AllocationPlan NotChecked(string missingField) =>
        new(Total: 0m, Unit: AllocationUnit.PrescribingUnits, Windows: [], NotChecked: true, MissingField: missingField);
}
