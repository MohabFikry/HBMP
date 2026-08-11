using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

/// <summary>
/// The daily cap (0025, design 42 §7 rule 13) — how many patients a clinician will take at a clinic on a day.
///
/// <para>The platform had no way to say this. Capacity was implicit in
/// <c>slot_minutes × (end_time − start_time)</c>, so the only way to limit a clinician to twenty patients was
/// to shorten their day — which also moved when they finish, and told the desk a different thing from what was
/// meant.</para>
///
/// <para>The cap is applied inside <see cref="SlotGeneration"/> rather than filtered afterwards, because
/// design 42 §7 rule 5 makes that function the single place availability is computed. A cap applied anywhere
/// else would be a second opinion on whether a slot exists, and the way that failure presents is a patient
/// holding an appointment the clinic will not honour.</para>
/// </summary>
public class DailyCapacityTests
{
    private static readonly TimeSpan Cairo = TimeSpan.FromHours(2);

    /// <summary>2026-07-23 is a Thursday.</summary>
    private static readonly DateOnly Thursday = new(2026, 7, 23);

    private static ProviderAvailability Avail(string start, string end, int minutes, int? cap = null) => new()
    {
        AvailabilityId = Guid.NewGuid(), ProviderId = Guid.NewGuid(), LocationId = Guid.NewGuid(),
        DoctorId = Guid.NewGuid(), BranchId = Guid.NewGuid(),
        DayOfWeek = DayOfWeek.Thursday,
        StartTime = TimeOnly.Parse(start), EndTime = TimeOnly.Parse(end), SlotMinutes = minutes,
        MaxPerDay = cap,
    };

    private static IReadOnlyList<AppointmentSlot> On(ProviderAvailability a, params RosterException[] exceptions) =>
        SlotGeneration.Generate(a, Thursday, Thursday, Cairo, null, exceptions);

    [Fact]
    public void No_cap_generates_the_whole_window_exactly_as_before()
    {
        // The regression that matters most: every rule that existed before 0025 has a NULL cap, and must
        // behave identically. A migration that quietly changed how many slots a clinic offers would be
        // discovered by patients.
        On(Avail("09:00", "12:00", 30)).Should().HaveCount(6);
    }

    [Fact]
    public void A_cap_below_the_windows_count_truncates_the_day()
    {
        // Six slots' worth of window, four patients' worth of clinician.
        var slots = On(Avail("09:00", "12:00", 30, cap: 4));

        slots.Should().HaveCount(4);
        // The EARLIEST four. A cap shortens the day from the end, so the clinic still starts when it says it
        // starts — truncating from the front would move a 09:00 clinic to 10:30 with nothing saying so.
        slots[0].SlotStart.Should().Be(new DateTimeOffset(2026, 7, 23, 9, 0, 0, Cairo));
        slots[^1].SlotEnd.Should().Be(new DateTimeOffset(2026, 7, 23, 11, 0, 0, Cairo));
    }

    [Fact]
    public void A_cap_above_the_windows_count_changes_nothing()
    {
        // "Up to thirty" against a window offering six is not an instruction to find another twenty-four.
        On(Avail("09:00", "12:00", 30, cap: 30)).Should().HaveCount(6);
    }

    [Fact]
    public void The_cap_applies_PER_DATE_not_across_the_generated_range()
    {
        // Generating a month must not exhaust the cap on the first Thursday. It is a daily limit; the range
        // is only how far ahead the calendar is being built.
        var a = Avail("09:00", "12:00", 30, cap: 2);
        var slots = SlotGeneration.Generate(a, Thursday, Thursday.AddDays(14), Cairo);

        slots.Should().HaveCount(6, "three Thursdays in the range, two slots each");
        slots.Select(s => s.SlotStart.Date).Distinct().Should().HaveCount(3);
    }

    [Fact]
    public void THE_ONE_THAT_MATTERS_the_cap_spans_every_window_the_day_offers()
    {
        // A morning pattern plus an ad-hoc afternoon clinic. A doctor capped at four ends the day at four —
        // not four per session. Applying the cap per window would DOUBLE it on precisely the day somebody
        // added extra capacity, which is the day the cap is most load-bearing.
        var a = Avail("09:00", "12:00", 30, cap: 4);
        var adHoc = new RosterException
        {
            ExceptionId = Guid.NewGuid(), TenantId = "t1", BranchId = a.BranchId, PractitionerId = a.DoctorId,
            DateFrom = Thursday, DateTo = Thursday, Kind = RosterExceptionKind.AdHocClinic,
            StartTime = new TimeOnly(14, 0), EndTime = new TimeOnly(17, 0), Reason = "extra clinic",
        };

        var slots = On(a, adHoc);

        slots.Should().HaveCount(4);
        // …and it fills from the MORNING, because the windows are taken in time order. Which four you get is
        // not a detail: "the first four of the morning" and "the last four of the afternoon" are the same
        // count and a different day for the person being booked.
        slots.Should().OnlyContain(s => s.SlotStart.ToOffset(Cairo).Hour < 12);
    }

    [Fact]
    public void The_cap_is_applied_AFTER_leave_is_subtracted()
    {
        // Leave removes the morning; the cap is four. The answer is the afternoon's four, not "the first four
        // slots of the day, which are cancelled". Order matters here and only shows up in a case like this.
        var a = Avail("09:00", "17:00", 60, cap: 4);
        var leave = new RosterException
        {
            ExceptionId = Guid.NewGuid(), TenantId = "t1", PractitionerId = a.DoctorId,
            DateFrom = Thursday, DateTo = Thursday, Kind = RosterExceptionKind.Leave,
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(13, 0), Reason = "half day",
        };

        var slots = On(a, leave);

        slots.Should().HaveCount(4);
        slots.Should().OnlyContain(s => s.SlotStart.ToOffset(Cairo).Hour >= 13);
    }

    [Fact]
    public void A_whole_day_closure_still_wins_over_any_cap()
    {
        var a = Avail("09:00", "12:00", 30, cap: 4);
        var closed = new RosterException
        {
            ExceptionId = Guid.NewGuid(), TenantId = "t1", BranchId = a.BranchId,
            DateFrom = Thursday, DateTo = Thursday, Kind = RosterExceptionKind.ClinicClosed,
            Reason = "burst pipe",
        };

        On(a, closed).Should().BeEmpty();
    }

    // ---- the two counts the roster screen shows ---------------------------------------------------------

    [Fact]
    public void WindowSlotCount_drops_the_trailing_partial_slot()
    {
        // 09:00–11:20 at thirty minutes is four slots and a twenty-minute stub nobody can be booked into.
        SlotGeneration.WindowSlotCount(new TimeOnly(9, 0), new TimeOnly(11, 20), 30).Should().Be(4);
        // Matches what Generate actually emits — the two must not be able to disagree, since the roster shows
        // one and the calendar is built from the other.
        On(Avail("09:00", "11:20", 30)).Should().HaveCount(4);
    }

    [Fact]
    public void WindowSlotCount_refuses_nonsense_rather_than_throwing()
    {
        SlotGeneration.WindowSlotCount(new TimeOnly(17, 0), new TimeOnly(9, 0), 30).Should().Be(0);
        SlotGeneration.WindowSlotCount(new TimeOnly(9, 0), new TimeOnly(17, 0), 0).Should().Be(0);
    }

    [Fact]
    public void EffectiveSlotsPerDay_is_the_smaller_of_the_window_and_the_cap()
    {
        SlotGeneration.EffectiveSlotsPerDay(new TimeOnly(9, 0), new TimeOnly(12, 0), 30, null).Should().Be(6);
        SlotGeneration.EffectiveSlotsPerDay(new TimeOnly(9, 0), new TimeOnly(12, 0), 30, 4).Should().Be(4);
        SlotGeneration.EffectiveSlotsPerDay(new TimeOnly(9, 0), new TimeOnly(12, 0), 30, 30).Should().Be(6);
    }
}
