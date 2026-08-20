using FluentAssertions;

namespace Mersal.Prescribing.Tests;

/// <summary>
/// 30.3 — re-allocating a chronic script whose windows have already started (design 46 §4).
///
/// <para><b>The principle, and every test below is a way of stating it: what was dispensed is a fact and is
/// never recalculated.</b> The amendment recomputes the TOTAL from the new duration, subtracts what was
/// actually handed over, and splits only the remainder — so the sum invariant becomes
/// <c>dispensed + Σ(new windows) == newTotal</c>, exactly.</para>
///
/// <para>The worked cases are phase-30 Gate 3's, verbatim, because they are the specification.</para>
/// </summary>
public class ChronicAmendmentTests
{
    /// <summary>The prompt's script: 90 days, monthly, 3 units/day ⇒ 270 units over three windows of 90.</summary>
    private static AllocationRequest NinetyDays(int days = 90, int frequencyMonths = 1) =>
        new(DosePerAdministration: 1, TimesPerDay: 3, DurationDays: days,
            FrequencyMonths: frequencyMonths, IsPackSplittable: true, PackContent: null);

    [Fact]
    public void The_original_script_is_270_over_three_windows_of_90()
    {
        // The baseline the amendments below are measured against. If this drifts, so does every case.
        var plan = ChronicAllocation.Plan(NinetyDays());
        plan.Total.Should().Be(270);
        plan.Windows.Should().Equal(90, 90, 90);
    }

    [Fact]
    public void Shortening_90_days_to_60_leaves_ONE_remaining_window_of_90_and_a_total_of_180()
    {
        // Gate 3's first worked case, verbatim: "90 days monthly, window 1 (90 units) dispensed, amended to
        // 60 days -> window 1 untouched, one remaining window of 90, total 180."
        var result = ChronicAmendment.Reallocate(
            NinetyDays(days: 60), alreadyDispensed: 90, windowsAlreadyStarted: 1);

        result.Outcome.Should().Be(AmendmentOutcome.Reallocated);
        result.NewTotal.Should().Be(180);
        result.AlreadyDispensed.Should().Be(90);
        result.RemainingWindows.Should().Equal(90);
        result.RemainingWindows.Sum().Should().Be(result.NewTotal - result.AlreadyDispensed);
    }

    [Fact]
    public void Extending_90_days_to_120_re_allocates_the_remaining_three_windows()
    {
        // "same script amended to 120 days monthly -> windows 2-4 re-allocated summing to the new remainder."
        var result = ChronicAmendment.Reallocate(
            NinetyDays(days: 120), alreadyDispensed: 90, windowsAlreadyStarted: 1);

        result.Outcome.Should().Be(AmendmentOutcome.Reallocated);
        result.NewTotal.Should().Be(360);
        result.RemainingWindows.Should().HaveCount(3);
        result.RemainingWindows.Should().Equal(90, 90, 90);
    }

    [Fact]
    public void A_new_total_below_what_was_already_dispensed_is_REFUSED()
    {
        // "amend to a total below 90 -> refused." 20 days x 3/day = 60 < 90 already handed over. Refusing is
        // the only honest answer: the alternative is a record claiming the patient returned 30 units.
        var result = ChronicAmendment.Reallocate(
            NinetyDays(days: 20), alreadyDispensed: 90, windowsAlreadyStarted: 1);

        result.Outcome.Should().Be(AmendmentOutcome.BelowDispensed);
        result.RemainingWindows.Should().BeEmpty("a refusal allocates nothing — a zero would read as a real "
                                              + "allocation of none");
    }

    [Fact]
    public void Reducing_to_one_month_or_less_asks_the_chronic_question_rather_than_deciding_it()
    {
        // "amend 90 days to 25 days -> chronic-definition prompt." NOT a silent conversion, and not a flat
        // refusal: design 46 §4 says the system must not keep a "chronic" script that is not chronic, and a
        // prescriber who got the duration wrong must still be able to fix it.
        var result = ChronicAmendment.Reallocate(
            NinetyDays(days: 25), alreadyDispensed: 0, windowsAlreadyStarted: 0);

        result.Outcome.Should().Be(AmendmentOutcome.NoLongerChronic);
        result.RemainingWindows.Should().BeEmpty();
    }

    [Fact]
    public void Exactly_thirty_days_is_ONE_month_and_so_is_no_longer_chronic()
    {
        // The boundary, and it is strict: design 45 §5's rule is "greater than one month".
        ChronicAmendment.Reallocate(NinetyDays(days: 30), 0, 0)
            .Outcome.Should().Be(AmendmentOutcome.NoLongerChronic);
        ChronicAmendment.Reallocate(NinetyDays(days: 31), 0, 0)
            .Outcome.Should().Be(AmendmentOutcome.Reallocated);
    }

    [Fact]
    public void Converting_to_acute_is_permitted_only_when_the_prescriber_says_so()
    {
        // The confirmation is a parameter, not an inference. With it, the script becomes acute and carries
        // NO refill schedule at all — an acute script with windows would make "is this chronic?" answerable
        // two ways.
        var result = ChronicAmendment.Reallocate(
            NinetyDays(days: 25), alreadyDispensed: 0, windowsAlreadyStarted: 0, convertToAcute: true);

        result.Outcome.Should().Be(AmendmentOutcome.ConvertedToAcute);
        result.NewTotal.Should().Be(75, "25 days at 3/day");
        result.RemainingWindows.Should().BeEmpty("an acute script has no windows");
    }

    [Fact]
    public void A_conversion_to_acute_still_refuses_a_total_below_what_was_dispensed()
    {
        // The confirmation flag authorises the CONVERSION, not un-dispensing. Two different questions, and
        // letting the flag answer both would make it a way to shrink a script past what the patient has.
        var result = ChronicAmendment.Reallocate(
            NinetyDays(days: 25), alreadyDispensed: 90, windowsAlreadyStarted: 1, convertToAcute: true);

        result.Outcome.Should().Be(AmendmentOutcome.BelowDispensed);
    }

    // ---------------------------------------------------------------- the sum invariant

    [Theory]
    [InlineData(120, 1, 90, 1)]
    [InlineData(180, 1, 90, 1)]
    [InlineData(365, 3, 270, 1)]
    [InlineData(100, 2, 0, 0)]
    [InlineData(200, 1, 150, 2)]
    [InlineData(91, 1, 100, 1)]
    public void The_allocation_ALWAYS_sums_exactly_to_the_new_total(
        int newDays, int frequencyMonths, decimal dispensed, int started)
    {
        // Invariant 4, and the one that breaks silently: each window looks like a sensible number and only
        // the sum is wrong. Asserted as an identity rather than against expected values, so it holds for
        // combinations nobody wrote a case for.
        var result = ChronicAmendment.Reallocate(
            NinetyDays(days: newDays, frequencyMonths: frequencyMonths), dispensed, started);

        if (result.Outcome != AmendmentOutcome.Reallocated) return;
        (result.AlreadyDispensed + result.RemainingWindows.Sum()).Should().Be(result.NewTotal,
            "dispensed + Σ(remaining windows) must equal the new total, exactly");
    }

    [Fact]
    public void An_indivisible_remainder_is_spread_by_largest_remainder_and_still_sums()
    {
        // 100 days at 3/day = 300; 90 already handed over; 210 left over 4 windows -> 53/53/52/52.
        // Rounded once at the TOTAL and distributed as integers, never rounded per window: 52.5 four times
        // would be 210 and undispensable, and 53 four times would be 212 — over-supplying the patient.
        var result = ChronicAmendment.Reallocate(
            NinetyDays(days: 100), alreadyDispensed: 90, windowsAlreadyStarted: 0);

        result.NewTotal.Should().Be(300);
        result.RemainingWindows.Should().Equal(53, 53, 52, 52);
        result.RemainingWindows.Sum().Should().Be(210);
        (result.RemainingWindows.Max() - result.RemainingWindows.Min())
            .Should().BeLessThanOrEqualTo(1, "no window may be more than one unit short of another");
    }

    [Fact]
    public void When_every_window_has_already_been_collected_there_is_nothing_left_to_allocate()
    {
        // Not an error: the prescriber shortened the script to exactly what has been handed over. The
        // remainder is legitimately zero, and an empty window list is the honest representation of it.
        var result = ChronicAmendment.Reallocate(
            NinetyDays(days: 90), alreadyDispensed: 270, windowsAlreadyStarted: 3);

        result.Outcome.Should().Be(AmendmentOutcome.Reallocated);
        result.RemainingWindows.Should().BeEmpty();
        result.AlreadyDispensed.Should().Be(result.NewTotal);
    }

    [Fact]
    public void More_windows_started_than_the_new_duration_holds_leaves_no_remaining_windows()
    {
        // The prescriber shortened the script below the point already reached. There is nothing further to
        // schedule; the remainder must be zero, and it is — because the total is unchanged by definition
        // when it equals what was dispensed. A negative window count is not a state this can reach.
        var result = ChronicAmendment.Reallocate(
            NinetyDays(days: 60), alreadyDispensed: 180, windowsAlreadyStarted: 3);

        result.Outcome.Should().Be(AmendmentOutcome.Reallocated);
        result.RemainingWindows.Should().BeEmpty();
    }

    [Fact]
    public void Missing_pack_data_refuses_to_compute_rather_than_guessing()
    {
        // Carried through from phase 29: "absence of data is never a clean result". An amendment must not be
        // the path that quietly invents an is_pack_splittable nobody recorded.
        var result = ChronicAmendment.Reallocate(
            new AllocationRequest(1, 3, 60, 1, IsPackSplittable: null, PackContent: null), 0, 0);

        result.Outcome.Should().Be(AmendmentOutcome.NotChecked);
        result.MissingField.Should().Be("is_pack_splittable");
        result.RemainingWindows.Should().BeEmpty();
    }
}
