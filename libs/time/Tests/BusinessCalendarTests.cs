using FluentAssertions;

namespace Mersal.Time.Tests;

/// <summary>Phase 18.A3 — the Cairo business date. Timestamps are UTC; business DATES are not.</summary>
public class BusinessCalendarTests
{
    private sealed class PinnedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static IBusinessCalendar At(string utcInstant) =>
        new BusinessCalendar(new PinnedClock(DateTimeOffset.Parse(utcInstant, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Coverage_validity_at_23_30_Cairo_evaluates_against_the_Cairo_date()
    {
        // 2026-07-27 21:30Z is 2026-07-28 00:30 in Cairo (UTC+3 in summer). Deriving the date from UTC
        // would answer "27 July" — so a coverage that expired on the 27th would still look valid, and a
        // limit reset due on the 28th would land a day late. Every evening, for hours.
        var calendar = At("2026-07-27T21:30:00Z");

        calendar.Today().Should().Be(new DateOnly(2026, 7, 28));
        DateOnly.FromDateTime(DateTime.Parse("2026-07-27T21:30:00Z", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal))
            .Should().Be(new DateOnly(2026, 7, 27), "which is exactly the wrong answer the platform used to give");
    }

    [Fact]
    public void Midday_agrees_with_UTC()
    {
        At("2026-07-27T09:00:00Z").Today().Should().Be(new DateOnly(2026, 7, 27));
    }

    [Fact]
    public void DateOf_stamps_the_business_date_of_a_recorded_instant()
    {
        var calendar = At("2026-01-01T00:00:00Z");

        // 2026-01-01 00:30Z is still 02:30 on the 1st in Cairo (UTC+2 in winter).
        calendar.DateOf(DateTimeOffset.Parse("2026-01-01T00:30:00Z", System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(new DateOnly(2026, 1, 1));
        // …but 2025-12-31 22:30Z has already rolled over to the 1st locally.
        calendar.DateOf(DateTimeOffset.Parse("2025-12-31T22:30:00Z", System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(new DateOnly(2026, 1, 1));
    }

    [Fact]
    public void The_zone_resolves_on_this_host()
    {
        BusinessCalendar.CairoZone.BaseUtcOffset.Should().BeGreaterThanOrEqualTo(TimeSpan.FromHours(2));
    }
}
