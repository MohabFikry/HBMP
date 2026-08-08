namespace Mersal.Prescribing;

/// <summary>
/// How much of a medicine a course actually needs, and how much is therefore dispensed.
/// </summary>
/// <param name="TotalUnits">Dose × times per day × days — what the patient consumes.</param>
/// <param name="DispenseQuantity">
/// What is handed over. Equal to <paramref name="TotalUnits"/> for a splittable pack; rounded UP to whole
/// packs for one that cannot be broken, because half an inhaler is not a thing anyone can dispense.
/// </param>
/// <param name="Packs">Whole packs, when the pack cannot be split. Null when it can.</param>
public sealed record QuantityPlan(decimal TotalUnits, decimal DispenseQuantity, decimal? Packs, decimal? PackSize);

/// <summary>The outcome of asking for a quantity: a plan, or the NAME of the fact that is missing.</summary>
/// <param name="Plan">Null when the quantity could not be computed.</param>
/// <param name="MissingField">
/// The master-data COLUMN that is absent — 'is_pack_splittable', 'pack_size' — or "dose" for a line with no
/// numeric dose yet. Named as the column because the person who fixes it reads the drug table, not a JSON
/// body.
/// </param>
public sealed record QuantityOutcome(QuantityPlan? Plan, string? MissingField)
{
    public static QuantityOutcome NotChecked(string field) => new(null, field);
    public static QuantityOutcome Of(QuantityPlan plan) => new(plan, null);
}

/// <summary>
/// 29.6 — THE quantity calculation (design 45 §6).
///
/// <para><b>Why it is here and not inside the check that reports it.</b> Three callers need the same number:
/// the prescribing composer, which fills the quantity field in as the doctor types; the validation check,
/// which tells them whether it is right; and the write path behind both. It lived inside
/// <c>QuantityChecks</c> as a formatted SENTENCE, so the composer could not use it at all — and a composer
/// that re-derived it in TypeScript would be a second implementation of the one piece of arithmetic that
/// decides how much medicine a person is handed.</para>
///
/// <para><b>Absence is never rounded away.</b> Every branch that cannot finish returns the NAME of the fact
/// it needed. Invariant 8: a guessed quantity is a dispensing error, and it is one that looks exactly like a
/// correct answer.</para>
/// </summary>
public static class QuantityMath
{
    public static QuantityOutcome Compute(
        decimal? doseAmount, int? timesPerDay, int? durationDays, bool? isPackSplittable, decimal? packSize)
    {
        // The commonest case on a free-text dose. Stated rather than passed silently: an "Ok" here would
        // read as "the quantity is right" about a quantity nobody computed.
        if (doseAmount is not { } dose || dose <= 0) return QuantityOutcome.NotChecked("dose");
        if (timesPerDay is not { } perDay || perDay <= 0) return QuantityOutcome.NotChecked("frequency");
        if (durationDays is not { } days || days <= 0) return QuantityOutcome.NotChecked("duration");

        var total = dose * perDay * days;

        if (isPackSplittable is not { } splittable) return QuantityOutcome.NotChecked("is_pack_splittable");

        // A splittable pack is dispensed to the exact requirement — 21 of a 20-tablet box is one box and one
        // strip's worth, and the pharmacy counts them out.
        if (splittable) return QuantityOutcome.Of(new QuantityPlan(total, total, null, packSize));

        // A pack that cannot be broken is dispensed WHOLE, so the total rounds up to a multiple of it — and
        // that needs the pack size. Without it the arithmetic is unfinishable, so it is reported, not
        // approximated.
        if (packSize is not { } size || size <= 0) return QuantityOutcome.NotChecked("pack_size");

        var packs = Math.Ceiling(total / size);
        return QuantityOutcome.Of(new QuantityPlan(total, packs * size, packs, size));
    }
}
