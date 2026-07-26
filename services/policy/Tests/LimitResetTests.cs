using FluentAssertions;
using Mersal.Policy.Domain;

namespace Mersal.Policy.Tests;

public class LimitResetTests
{
    private static CoverageLimit Limit(LimitType type, ResetPeriod period, decimal limit, decimal consumed, DateOnly? lastReset = null)
        => new() { LimitType = type, ResetPeriod = period, LimitValue = limit, ConsumedValue = consumed, LastResetOn = lastReset };

    [Fact]
    public void Remaining_is_limit_minus_consumed()
        => Limit(LimitType.Annual, ResetPeriod.Yearly, 1000m, 250m).Remaining.Should().Be(750m);

    [Theory]
    [InlineData(ResetPeriod.Monthly, "2026-03-15", "2026-03-01")]
    [InlineData(ResetPeriod.Quarterly, "2026-05-10", "2026-04-01")]  // Q2 starts April
    [InlineData(ResetPeriod.Yearly, "2026-07-22", "2026-01-01")]
    public void Period_start_boundaries(ResetPeriod period, string on, string expected)
        => LimitReset.PeriodStart(period, DateOnly.Parse(on)).Should().Be(DateOnly.Parse(expected));

    [Fact]
    public void Reset_is_due_when_period_boundary_passed_since_last_reset()
    {
        var l = Limit(LimitType.Annual, ResetPeriod.Monthly, 500m, 300m, lastReset: new DateOnly(2026, 6, 1));
        LimitReset.IsResetDue(l, new DateOnly(2026, 7, 5)).Should().BeTrue();   // new month
        LimitReset.IsResetDue(l, new DateOnly(2026, 6, 20)).Should().BeFalse(); // same month
    }

    [Fact]
    public void Lifetime_and_none_never_reset()
    {
        LimitReset.IsResetDue(Limit(LimitType.Lifetime, ResetPeriod.None, 1m, 1m), new DateOnly(2030, 1, 1)).Should().BeFalse();
        LimitReset.IsResetDue(Limit(LimitType.Annual, ResetPeriod.None, 1m, 1m), new DateOnly(2030, 1, 1)).Should().BeFalse();
    }

    [Fact]
    public void Apply_resets_consumed_to_zero_and_stamps_last_reset()
    {
        var l = Limit(LimitType.Annual, ResetPeriod.Monthly, 500m, 300m, lastReset: new DateOnly(2026, 6, 1));

        var changed = LimitReset.ApplyIfDue(l, new DateOnly(2026, 7, 5));

        changed.Should().BeTrue();
        l.ConsumedValue.Should().Be(0m);
        l.LastResetOn.Should().Be(new DateOnly(2026, 7, 1));
        l.Remaining.Should().Be(500m);
    }

    [Fact]
    public void Apply_is_noop_when_not_due()
    {
        var l = Limit(LimitType.Annual, ResetPeriod.Monthly, 500m, 300m, lastReset: new DateOnly(2026, 7, 1));
        LimitReset.ApplyIfDue(l, new DateOnly(2026, 7, 20)).Should().BeFalse();
        l.ConsumedValue.Should().Be(300m); // unchanged
    }

    // ── 18.A3 / audit R2 X10 — the first-ever reset must not wipe in-period consumption ────────────

    [Fact]
    public void A_reset_is_not_due_within_the_same_period_when_last_reset_is_null()
    {
        // This used to return TRUE for any consumed > 0, so the first run of the reset job handed a
        // member who had used 8 of their 10 annual visits all 10 back.
        var l = Limit(LimitType.Annual, ResetPeriod.Yearly, 10m, 8m, lastReset: null);

        LimitReset.IsResetDue(l, new DateOnly(2026, 7, 27)).Should().BeFalse();
        LimitReset.ApplyIfDue(l, new DateOnly(2026, 7, 27)).Should().BeFalse();
        l.ConsumedValue.Should().Be(8m, "in-period consumption survives");
    }

    [Fact]
    public void A_seeded_limit_resets_only_once_its_own_period_boundary_passes()
    {
        var effectiveFrom = new DateOnly(2026, 7, 10);
        var seeded = LimitReset.SeedLastResetOn(ResetPeriod.Monthly, LimitType.Annual, effectiveFrom);
        seeded.Should().Be(new DateOnly(2026, 7, 1), "the anchor is the start of the period containing the effective date");

        var l = Limit(LimitType.Annual, ResetPeriod.Monthly, 500m, 300m, lastReset: seeded);

        LimitReset.IsResetDue(l, new DateOnly(2026, 7, 31)).Should().BeFalse(); // still the seeded period
        LimitReset.IsResetDue(l, new DateOnly(2026, 8, 1)).Should().BeTrue();   // genuinely a new period
    }

    [Fact]
    public void Non_resetting_limits_are_seeded_with_no_anchor()
    {
        var on = new DateOnly(2026, 7, 10);
        LimitReset.SeedLastResetOn(ResetPeriod.None, LimitType.Annual, on).Should().BeNull();
        LimitReset.SeedLastResetOn(ResetPeriod.Yearly, LimitType.Lifetime, on).Should().BeNull();
    }
}
