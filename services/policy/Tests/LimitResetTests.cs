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
}
