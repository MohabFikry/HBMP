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
/// <param name="PackContent">How many prescribing units one box holds — the divisor everything here used.</param>
/// <param name="Boxes">
/// 31.2 — how many BOXES to hand over, which is what a pharmacy actually counts.
///
/// <para>NULL when the box's contents are not recorded. That is not a rare state and it is not defaulted:
/// the workbook gives no volume for "Lantus Solostar 100 I.U./ML 5 Pens", so how much insulin the box holds
/// is genuinely unknown, and three millilitres per pen — the usual fill — is a guess this refuses to make.
/// A wrong box count looks exactly like a right one at a dispensing counter.</para>
/// </param>
public sealed record QuantityPlan(
    decimal TotalUnits, decimal DispenseQuantity, decimal? Packs, decimal? PackContent, decimal? Boxes = null);

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
    /// <param name="packContent">
    /// 31.3 — how many PRESCRIBING UNITS one box holds: 24 tablets, 120 millilitres, 1500 IU.
    ///
    /// <para>It replaced <c>packSize</c>, which counts the catalogue's minor units and is therefore only the
    /// same number for the countable forms. Under the old divisor a 210 ml course of syrup — <c>pack_size =
    /// 1</c> for a 120 ml bottle — came out as 210 packs, and a box of insulin pens could not be divided at
    /// all. See <see cref="PackUnitRules"/> for where the content is derived.</para>
    ///
    /// <para>Null is a real answer and it is carried through as one.</para>
    /// </param>
    public static QuantityOutcome Compute(
        decimal? doseAmount, int? timesPerDay, int? durationDays, bool? isPackSplittable, decimal? packContent)
    {
        // The commonest case on a free-text dose. Stated rather than passed silently: an "Ok" here would
        // read as "the quantity is right" about a quantity nobody computed.
        if (doseAmount is not { } dose || dose <= 0) return QuantityOutcome.NotChecked("dose");
        if (timesPerDay is not { } perDay || perDay <= 0) return QuantityOutcome.NotChecked("frequency");
        if (durationDays is not { } days || days <= 0) return QuantityOutcome.NotChecked("duration");

        var total = dose * perDay * days;

        if (isPackSplittable is not { } splittable) return QuantityOutcome.NotChecked("is_pack_splittable");

        // Boxes round UP and never to zero: a ten-tablet course from a box of thirty is one box, and eight
        // boxes of seven is 56 against a 60-tablet course — four days short, which reads as a completed one.
        var boxes = packContent is > 0 ? Math.Ceiling(total / packContent.Value) : (decimal?)null;

        // A splittable pack is dispensed to the exact requirement — 21 of a 20-tablet box is one box and one
        // strip's worth, and the pharmacy counts them out.
        if (splittable) return QuantityOutcome.Of(new QuantityPlan(total, total, null, packContent, boxes));

        // A pack that cannot be broken is dispensed WHOLE, so the total rounds up to a multiple of what the
        // box HOLDS. Without that the arithmetic is unfinishable, so it is reported, not approximated — and
        // it is the box's contents that are needed, not its pack size: dividing 210 ml of syrup by a pack
        // size of 1 produced 210 bottles rather than an admission that the bottle's volume was unread.
        if (packContent is not { } content || content <= 0) return QuantityOutcome.NotChecked("pack_content");

        var packs = Math.Ceiling(total / content);
        return QuantityOutcome.Of(new QuantityPlan(total, packs * content, packs, content, boxes ?? packs));
    }
}
