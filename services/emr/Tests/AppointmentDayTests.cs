using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

/// <summary>
/// The board's day window. It used to be a UTC day while every rendered time was Africa/Cairo, so the query and
/// the screen disagreed by two hours at both ends — invisible during clinic hours and wrong on the early list.
/// </summary>
public class AppointmentDayTests
{
    // Fixed +02:00; Egypt reintroduced DST in 2023, so the DST case is covered separately with a varying offset.
    private static TimeSpan Cairo(DateOnly _) => TimeSpan.FromHours(2);

    [Fact]
    public void The_window_is_the_CAIRO_civil_day_not_the_utc_one()
    {
        // 09:00 Cairo on 22 July.
        var instant = new DateTimeOffset(2026, 7, 22, 7, 0, 0, TimeSpan.Zero);
        var (start, end) = AppointmentDay.CairoWindow(instant, Cairo);

        start.Should().Be(new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.FromHours(2)));
        end.Should().Be(new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.FromHours(2)));
        // Which is 22:00 UTC the previous day → 22:00 UTC today.
        start.UtcDateTime.Should().Be(new DateTime(2026, 7, 21, 22, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void An_early_morning_Cairo_appointment_is_on_TODAYS_board()
    {
        // 00:30 Cairo on 22 July == 22:30 UTC on 21 July. Under a UTC day this fell on the 21st and simply
        // disappeared from the 22nd's board.
        var appt = new DateTimeOffset(2026, 7, 21, 22, 30, 0, TimeSpan.Zero);
        var (start, end) = AppointmentDay.CairoWindow(new DateTimeOffset(2026, 7, 22, 7, 0, 0, TimeSpan.Zero), Cairo);

        (appt >= start && appt < end).Should().BeTrue("00:30 Cairo on the 22nd belongs to the 22nd");
    }

    [Fact]
    public void During_the_first_two_Cairo_hours_the_board_is_TODAY_not_yesterday()
    {
        // 01:00 Cairo on 22 July == 23:00 UTC on the 21st. `now.Date` in UTC would have said "the 21st".
        var (start, _) = AppointmentDay.CairoWindow(new DateTimeOffset(2026, 7, 21, 23, 0, 0, TimeSpan.Zero), Cairo);
        start.Should().Be(new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Tomorrows_early_Cairo_appointments_are_NOT_on_todays_board()
    {
        // 00:30 Cairo on the 23rd == 22:30 UTC on the 22nd, which a UTC day wrongly counted as the 22nd.
        var tomorrowEarly = new DateTimeOffset(2026, 7, 22, 22, 30, 0, TimeSpan.Zero);
        var (start, end) = AppointmentDay.CairoWindow(new DateTimeOffset(2026, 7, 22, 7, 0, 0, TimeSpan.Zero), Cairo);
        (tomorrowEarly >= start && tomorrowEarly < end).Should().BeFalse();
    }

    [Fact]
    public void A_day_that_straddles_a_dst_change_uses_each_sides_own_offset()
    {
        // Egypt's DST ends on the last Thursday of October; model +03:00 through 29 Oct and +02:00 after.
        TimeSpan varying(DateOnly d) => d <= new DateOnly(2026, 10, 29) ? TimeSpan.FromHours(3) : TimeSpan.FromHours(2);
        var (start, end) = AppointmentDay.CairoWindow(new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero), varying);

        start.Offset.Should().Be(TimeSpan.FromHours(3), "the day starts before the change");
        end.Offset.Should().Be(TimeSpan.FromHours(2), "and ends after it");
        // The window is a real 25 hours, not a naive 24 — which is the point of resolving both ends.
        (end - start).Should().Be(TimeSpan.FromHours(25));
    }

    [Fact]
    public void An_explicit_date_is_also_read_in_Cairo()
    {
        var (start, end) = AppointmentDay.CairoWindow(new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero), Cairo);
        start.Should().Be(new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.FromHours(2)));
        end.Should().Be(new DateTimeOffset(2026, 3, 11, 0, 0, 0, TimeSpan.FromHours(2)));
    }

    // ---- 14.5: the custom date RANGE on the reception board ------------------------------------------------

    [Fact]
    public void A_range_is_INCLUSIVE_of_the_last_day()
    {
        // "Sunday to Thursday" said out loud includes Thursday. A half-open range would silently drop
        // Thursday's evening clinic, and the desk would only find out when a patient arrived for an
        // appointment the board said did not exist.
        var from = new DateTimeOffset(2026, 7, 19, 9, 0, 0, TimeSpan.FromHours(2));
        var to = new DateTimeOffset(2026, 7, 23, 9, 0, 0, TimeSpan.FromHours(2));

        var (start, end) = AppointmentDay.CairoRange(from, to, Cairo);

        start.Should().Be(new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.FromHours(2)));
        end.Should().Be(new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.FromHours(2)),
            "the range ends at the END of the last day, not at its start");

        var thursdayEvening = new DateTimeOffset(2026, 7, 23, 20, 30, 0, TimeSpan.FromHours(2));
        (thursdayEvening >= start && thursdayEvening < end).Should().BeTrue();
    }

    [Fact]
    public void A_single_day_range_matches_the_single_day_window()
    {
        // from == to must behave exactly as the existing one-day filter, or "today" would mean two different
        // things depending on which control the operator used.
        var day = new DateTimeOffset(2026, 7, 22, 13, 0, 0, TimeSpan.FromHours(2));

        AppointmentDay.CairoRange(day, day, Cairo).Should().Be(AppointmentDay.CairoWindow(day, Cairo));
    }

    [Fact]
    public void An_early_morning_appointment_on_the_last_day_is_inside_the_range()
    {
        // 00:30 Cairo on the closing day — the mirror of the single-day case above, at the far end.
        var from = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.FromHours(2));
        var to = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.FromHours(2));
        var (start, end) = AppointmentDay.CairoRange(from, to, Cairo);

        var appt = new DateTimeOffset(2026, 7, 21, 22, 30, 0, TimeSpan.Zero);   // 00:30 Cairo on the 22nd
        (appt >= start && appt < end).Should().BeTrue();
    }
}
