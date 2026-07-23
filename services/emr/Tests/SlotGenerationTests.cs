using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

public class SlotGenerationTests
{
    private static readonly TimeSpan Cairo = TimeSpan.FromHours(2);

    private static ProviderAvailability Avail(DayOfWeek day, string start, string end, int minutes) => new()
    {
        AvailabilityId = Guid.NewGuid(), ProviderId = Guid.NewGuid(), LocationId = Guid.NewGuid(),
        DayOfWeek = day, StartTime = TimeOnly.Parse(start), EndTime = TimeOnly.Parse(end), SlotMinutes = minutes,
    };

    [Fact]
    public void Generates_whole_slots_within_the_daily_window()
    {
        // 2026-07-23 is a Thursday. 09:00–10:00, 15-min slots → 4 slots.
        var a = Avail(DayOfWeek.Thursday, "09:00", "10:00", 15);
        var slots = SlotGeneration.Generate(a, new DateOnly(2026, 7, 23), new DateOnly(2026, 7, 23), Cairo);

        slots.Should().HaveCount(4);
        slots[0].SlotStart.Should().Be(new DateTimeOffset(2026, 7, 23, 9, 0, 0, Cairo));
        slots[0].SlotEnd.Should().Be(new DateTimeOffset(2026, 7, 23, 9, 15, 0, Cairo));
        slots[^1].SlotEnd.Should().Be(new DateTimeOffset(2026, 7, 23, 10, 0, 0, Cairo));
        slots.Should().OnlyContain(s => s.ProviderId == a.ProviderId && s.LocationId == a.LocationId);
    }

    [Fact]
    public void Drops_a_trailing_partial_remainder()
    {
        // 09:00–09:50 with 20-min slots → 09:00, 09:20 (09:40–10:00 would overflow) → 2 slots.
        var a = Avail(DayOfWeek.Thursday, "09:00", "09:50", 20);
        var slots = SlotGeneration.Generate(a, new DateOnly(2026, 7, 23), new DateOnly(2026, 7, 23), Cairo);
        slots.Should().HaveCount(2);
    }

    [Fact]
    public void Emits_only_matching_weekdays_across_a_range()
    {
        // One week: only the two Thursdays produce slots (23rd and 30th July 2026).
        var a = Avail(DayOfWeek.Thursday, "09:00", "10:00", 30); // 2 slots per day
        var slots = SlotGeneration.Generate(a, new DateOnly(2026, 7, 20), new DateOnly(2026, 8, 2), Cairo);
        slots.Should().HaveCount(4);
        slots.Select(s => s.SlotStart.Day).Distinct().Should().BeEquivalentTo(new[] { 23, 30 });
    }

    [Fact]
    public void Rejects_nonpositive_slot_minutes_and_inverted_window()
    {
        var bad = Avail(DayOfWeek.Monday, "09:00", "10:00", 0);
        Action zero = () => SlotGeneration.Generate(bad, new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 20), Cairo);
        zero.Should().Throw<ArgumentOutOfRangeException>();

        var inverted = Avail(DayOfWeek.Monday, "10:00", "09:00", 15);
        Action rev = () => SlotGeneration.Generate(inverted, new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 20), Cairo);
        rev.Should().Throw<ArgumentException>();
    }
}
