using FluentAssertions;
using Mersal.Prescribing;

namespace Mersal.Prescribing.Tests;

/// <summary>
/// 29.6 — the one quantity calculation (design 45 §6).
///
/// <para>Its whole reason for existing is that THREE callers need the same number and used to have no way to
/// share it: the composer that fills the field in, the check that grades it, and the write path behind both.
/// So these assert the NUMBERS, not a sentence — a formatted message is what the composer could not use.</para>
/// </summary>
public class QuantityMathTests
{
    [Fact]
    public void A_splittable_pack_is_dispensed_to_the_exact_requirement()
    {
        // 1 tablet three times a day for 7 days = 21. The box holds 20, and the pharmacy counts out 21 —
        // rounding to two whole boxes would hand over 40 and charge the beneficiary's benefit for them.
        var o = QuantityMath.Compute(doseAmount: 1m, timesPerDay: 3, durationDays: 7,
            isPackSplittable: true, packSize: 20m);

        o.MissingField.Should().BeNull();
        o.Plan!.TotalUnits.Should().Be(21m);
        o.Plan.DispenseQuantity.Should().Be(21m);
        o.Plan.Packs.Should().BeNull("nothing is counted in packs when the pack can be opened");
    }

    [Fact]
    public void A_pack_that_cannot_be_split_rounds_UP_to_whole_packs()
    {
        // 2 puffs twice a day for 30 days = 120 puffs; a 200-puff inhaler cannot be halved, so one inhaler.
        var o = QuantityMath.Compute(doseAmount: 2m, timesPerDay: 2, durationDays: 30,
            isPackSplittable: false, packSize: 200m);

        o.Plan!.TotalUnits.Should().Be(120m);
        o.Plan.Packs.Should().Be(1m);
        o.Plan.DispenseQuantity.Should().Be(200m, "a whole inhaler is what leaves the counter");
    }

    [Fact]
    public void Rounding_up_never_leaves_the_patient_short()
    {
        // 201 puffs needed, 200 to a pack — TWO packs. Rounding to nearest would send them home one dose
        // short of finishing the course, which is the error that reads as a completed treatment.
        var o = QuantityMath.Compute(doseAmount: 1m, timesPerDay: 1, durationDays: 201,
            isPackSplittable: false, packSize: 200m);

        o.Plan!.Packs.Should().Be(2m);
        o.Plan.DispenseQuantity.Should().Be(400m);
    }

    [Theory]
    [InlineData(null, 3, 7, "dose")]
    [InlineData(1.0, null, 7, "frequency")]
    [InlineData(1.0, 3, null, "duration")]
    [InlineData(0.0, 3, 7, "dose")]
    public void An_incomplete_line_names_what_is_missing(double? dose, int? perDay, int? days, string field)
    {
        var o = QuantityMath.Compute((decimal?)dose, perDay, days, isPackSplittable: true, packSize: 20m);

        o.Plan.Should().BeNull();
        o.MissingField.Should().Be(field);
    }

    [Fact]
    public void An_unknown_splittability_names_the_COLUMN_rather_than_guessing()
    {
        // Invariant 8, and the reason the field is named as a database column: the person who fixes this
        // reads the drug table, not a JSON body.
        var o = QuantityMath.Compute(1m, 3, 7, isPackSplittable: null, packSize: 20m);

        o.Plan.Should().BeNull();
        o.MissingField.Should().Be("is_pack_splittable");
    }

    [Fact]
    public void An_unsplittable_pack_with_no_size_cannot_be_counted_and_says_so()
    {
        // The arithmetic is genuinely unfinishable here. Falling back to the raw total would dispense 120
        // "puffs" — a number no pharmacy can act on, presented as though it were a quantity.
        var o = QuantityMath.Compute(2m, 2, 30, isPackSplittable: false, packSize: null);

        o.Plan.Should().BeNull();
        o.MissingField.Should().Be("pack_size");
    }

    [Fact]
    public void A_splittable_pack_with_no_size_still_answers()
    {
        // Pack size is only needed to count WHOLE packs. A splittable pack is dispensed to the requirement,
        // so a missing size costs nothing and refusing here would block a line that is perfectly computable.
        var o = QuantityMath.Compute(1m, 2, 10, isPackSplittable: true, packSize: null);

        o.Plan!.DispenseQuantity.Should().Be(20m);
    }

    [Fact]
    public void A_fractional_dose_is_carried_through_rather_than_rounded_at_each_step()
    {
        // Half a tablet twice a day for 30 days = 30 tablets, not 60 and not 15. Rounding the DOSE first is
        // how "half a tablet" quietly becomes a whole one.
        var o = QuantityMath.Compute(0.5m, 2, 30, isPackSplittable: true, packSize: 20m);

        o.Plan!.TotalUnits.Should().Be(30m);
    }
}
