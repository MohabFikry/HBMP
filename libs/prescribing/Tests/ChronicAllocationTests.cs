using FluentAssertions;
using Mersal.Prescribing;

namespace Mersal.Prescribing.Tests;

/// <summary>
/// 29.5 / design 45 §5 — the chronic allocation.
///
/// <para><b>INVARIANT 5: a chronic allocation sums EXACTLY to the prescribed total.</b> Round once at the
/// total, never per window. Rounding each window independently lets the sum drift ABOVE the prescribed
/// amount, which over-supplies the patient and over-consumes their benefit — and it does so silently, because
/// every individual window looks like a sensible number.</para>
/// </summary>
public class ChronicAllocationTests
{
    // ---- Worked cases from design 45 §5 -------------------------------------------------------------------

    [Fact]
    public void Ninety_days_monthly_one_tablet_three_times_daily_is_three_windows_of_ninety()
    {
        // The document's own worked example: 1 × 3 × 90 = 270 tablets, over ⌈90 ÷ 30⌉ = 3 windows.
        var plan = ChronicAllocation.Plan(new AllocationRequest(
            DosePerAdministration: 1m, TimesPerDay: 3, DurationDays: 90,
            FrequencyMonths: 1, IsPackSplittable: true, PackSize: 20m));

        plan.Total.Should().Be(270m);
        plan.Windows.Should().Equal(90m, 90m, 90m);
    }

    [Fact]
    public void One_hundred_units_over_three_windows_is_thirty_four_thirty_three_thirty_three()
    {
        // Largest-remainder, HIGHEST FIRST — design 45 §5 states the order, so it is asserted as a sequence
        // and not as a multiset. A doctor reading the schedule before submitting sees the bigger collection
        // first, which is also the one that has already been dispensed if the script is interrupted.
        ChronicAllocation.Split(total: 100m, windows: 3).Should().Equal(34m, 33m, 33m);
    }

    [Fact]
    public void Ninety_days_every_two_months_is_two_windows_sixty_days_worth_then_thirty()
    {
        // ⌈90 ÷ 60⌉ = 2. The split is by TOTAL, not by days — but with a uniform daily dose the two coincide,
        // and the document's phrasing ("60 days' worth, then 30") is the check that they do.
        var plan = ChronicAllocation.Plan(new AllocationRequest(
            DosePerAdministration: 1m, TimesPerDay: 1, DurationDays: 90,
            FrequencyMonths: 2, IsPackSplittable: true, PackSize: 30m));

        plan.Total.Should().Be(90m);
        plan.Windows.Should().Equal(45m, 45m);
    }

    [Fact]
    public void A_non_splittable_inhaler_allocates_whole_items_summing_to_the_rounded_total()
    {
        // 2 puffs × 2/day × 90 days = 360 puffs. A 200-puff canister cannot be split, so the TOTAL rounds UP
        // to 2 canisters — once, before the split — and the windows are whole canisters.
        var plan = ChronicAllocation.Plan(new AllocationRequest(
            DosePerAdministration: 2m, TimesPerDay: 2, DurationDays: 90,
            FrequencyMonths: 1, IsPackSplittable: false, PackSize: 200m));

        plan.Total.Should().Be(2m, "360 puffs is two whole canisters, rounded UP at the total");
        plan.Unit.Should().Be(AllocationUnit.WholePacks);
        plan.Windows.Sum().Should().Be(plan.Total);
        plan.Windows.Should().OnlyContain(w => w == Math.Floor(w), "a canister cannot be cut in three");
    }

    // ---- The invariant -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(100, 3)] [InlineData(270, 3)] [InlineData(1, 3)] [InlineData(2, 3)]
    [InlineData(7, 4)] [InlineData(99, 7)] [InlineData(1000, 6)] [InlineData(5, 5)]
    [InlineData(0, 3)] [InlineData(13, 1)] [InlineData(23, 12)]
    public void The_allocation_always_sums_exactly_to_the_total(int total, int windows)
    {
        // INVARIANT 5, as a property rather than as four examples. Four worked cases do not establish "always",
        // and "always" is the only version of this statement that is worth anything: the failure it prevents
        // is a script that hands out more than was prescribed.
        var split = ChronicAllocation.Split(total, windows);

        split.Should().HaveCount(windows);
        split.Sum().Should().Be(total);
        split.Should().OnlyContain(q => q >= 0, "no window is ever negative");
    }

    [Fact]
    public void The_allocation_never_rounds_per_window()
    {
        // The mechanism behind the invariant, stated so a future refactor cannot satisfy the sum by accident.
        // 100/3 rounded per window would be 33.33→34 three times = 102, two more than prescribed. Two tablets
        // is not much; two tablets on every chronic script in a clinic is a benefit line nobody can reconcile.
        var split = ChronicAllocation.Split(total: 100m, windows: 3);

        split.Sum().Should().Be(100m);
        split.Sum().Should().NotBe(102m);
    }

    [Fact]
    public void Windows_differ_by_at_most_one_so_no_month_is_conspicuously_short()
    {
        // Largest-remainder's defining property. A split of 100 into 50/25/25 also sums to 100 and would be
        // clinically wrong: the patient runs out.
        var split = ChronicAllocation.Split(total: 100m, windows: 3);

        (split.Max() - split.Min()).Should().BeLessThanOrEqualTo(1m);
    }

    [Fact]
    public void Highest_first_is_the_order_not_merely_the_multiset()
    {
        ChronicAllocation.Split(total: 100m, windows: 3).Should().BeInDescendingOrder();
        ChronicAllocation.Split(total: 7m, windows: 4).Should().Equal(2m, 2m, 2m, 1m);
    }

    // ---- Window count -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(90, 1, 3)]     // 90 days monthly     → 3
    [InlineData(90, 2, 2)]     // 90 days 2-monthly   → 2
    [InlineData(90, 3, 1)]     // 90 days 3-monthly   → 1
    [InlineData(180, 3, 2)]    // 180 days 3-monthly  → 2
    [InlineData(31, 1, 2)]     // 31 days monthly     → 2 (the ceiling, not a rounding down)
    public void Window_count_is_the_ceiling_of_duration_over_the_frequency_period(
        int durationDays, int frequencyMonths, int expected)
    {
        ChronicAllocation.WindowCount(durationDays, frequencyMonths).Should().Be(expected);
    }

    // ---- Refusals -----------------------------------------------------------------------------------------

    [Fact]
    public void A_split_into_zero_or_fewer_windows_is_refused_rather_than_returning_nothing()
    {
        // Returning an empty list would make the sum trivially "correct" at zero, and the caller would store a
        // script with no windows that nobody could ever collect against.
        var act = () => ChronicAllocation.Split(total: 100m, windows: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_negative_total_is_refused()
    {
        var act = () => ChronicAllocation.Split(total: -1m, windows: 3);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_duration_of_one_month_or_less_is_not_chronic()
    {
        // Design 45 §5: "Chronic requires a duration greater than one month. A 14-day course is not chronic;
        // reject with a clear message rather than silently accepting."
        ChronicAllocation.IsChronicDuration(14).Should().BeFalse();
        ChronicAllocation.IsChronicDuration(30).Should().BeFalse();
        ChronicAllocation.IsChronicDuration(31).Should().BeTrue();
    }

    // ---- Missing unit data ---------------------------------------------------------------------------------

    [Fact]
    public void Missing_pack_data_on_a_non_splittable_form_yields_NotChecked_naming_the_field()
    {
        // Invariant 8: "Missing unit data ⇒ NotChecked, NEVER a guessed quantity." A non-splittable form
        // cannot be converted to whole packs without a pack size, and assuming one would produce a plausible
        // number that is wrong — which is a dispensing error, not a rounding error.
        var plan = ChronicAllocation.Plan(new AllocationRequest(
            DosePerAdministration: 2m, TimesPerDay: 2, DurationDays: 90,
            FrequencyMonths: 1, IsPackSplittable: false, PackSize: null));

        plan.NotChecked.Should().BeTrue();
        plan.MissingField.Should().Be("pack_size");
        plan.Windows.Should().BeEmpty("a quantity that could not be computed is absent, never zero");
    }

    [Fact]
    public void Unknown_splittability_yields_NotChecked_rather_than_assuming_splittable()
    {
        // Assuming splittable is the DANGEROUS default: it silently permits a fractional inhaler.
        var plan = ChronicAllocation.Plan(new AllocationRequest(
            DosePerAdministration: 1m, TimesPerDay: 3, DurationDays: 90,
            FrequencyMonths: 1, IsPackSplittable: null, PackSize: 20m));

        plan.NotChecked.Should().BeTrue();
        plan.MissingField.Should().Be("is_pack_splittable");
    }
}
