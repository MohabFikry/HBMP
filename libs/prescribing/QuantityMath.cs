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
/// <param name="Boxes">
/// 31.2 — how many BOXES to hand over, which is what a pharmacy actually counts.
///
/// <para>NULL when the question has no answer, and it often has none: <c>pack_size</c> counts the
/// catalogue's MINOR UNITS — the countable items in a box — and that is only the same thing the dose counts
/// for forms like tablets and ampoules. "Lantus Solostar 100 I.U./ML 5 Pens" holds 5 PENS and is dosed in
/// IU; 180 IU over a pack of 5 divides to 36 boxes, when 180 IU is less than a single 300-IU pen. Withheld
/// rather than printed.</para>
/// </param>
public sealed record QuantityPlan(
    decimal TotalUnits, decimal DispenseQuantity, decimal? Packs, decimal? PackSize, decimal? Boxes = null);

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
    /// <param name="packCountsDoses">
    /// 31.2 — whether <paramref name="packSize"/> counts the SAME thing the dose does.
    ///
    /// <para>True for tablets, capsules, sachets, ampoules and the rest of the countable forms. False for a
    /// bottle of syrup dosed in millilitres or a box of pens dosed in IU, where the pack counts CONTAINERS.
    /// Defaults false, which is the safe direction: a box count is simply not offered rather than being
    /// computed from two different units and presented as fact.</para>
    /// </param>
    public static QuantityOutcome Compute(
        decimal? doseAmount, int? timesPerDay, int? durationDays, bool? isPackSplittable, decimal? packSize,
        bool packCountsDoses = false)
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
        var boxes = packCountsDoses && packSize is > 0 ? Math.Ceiling(total / packSize.Value) : (decimal?)null;

        // A splittable pack is dispensed to the exact requirement — 21 of a 20-tablet box is one box and one
        // strip's worth, and the pharmacy counts them out.
        if (splittable) return QuantityOutcome.Of(new QuantityPlan(total, total, null, packSize, boxes));

        // A pack that cannot be broken is dispensed WHOLE, so the total rounds up to a multiple of it — and
        // that needs the pack size. Without it the arithmetic is unfinishable, so it is reported, not
        // approximated.
        if (packSize is not { } size || size <= 0) return QuantityOutcome.NotChecked("pack_size");

        var packs = Math.Ceiling(total / size);
        return QuantityOutcome.Of(new QuantityPlan(total, packs * size, packs, size, boxes ?? packs));
    }
}
