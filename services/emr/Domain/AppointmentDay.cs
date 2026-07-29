namespace Mersal.Emr.Domain;

/// <summary>
/// Which appointments belong to "a day" on the reception board.
///
/// This was a UTC day — <c>now.Date</c> to <c>+1</c> at offset zero — while every time the board RENDERS is
/// formatted in Africa/Cairo (18.D2 / U7 fixed the display and left the query behind). Cairo is UTC+2, so the
/// two disagree by two hours at both ends: an 01:00 Cairo appointment sits in the previous UTC day and vanished
/// from today's board, and for the first two hours of every Cairo day the board still showed yesterday. Daytime
/// clinic hours hide it, which is what makes it the kind of bug that surfaces on the early list and nowhere
/// else. The window is now the Cairo civil day, matching what the desk reads on screen.
/// </summary>
public static class AppointmentDay
{
    /// <summary>The half-open [start, end) window covering the Cairo civil day containing <paramref name="instant"/>.
    /// <paramref name="offsetFor"/> supplies the zone's offset for a given date (DST-aware at the call site).</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) CairoWindow(
        DateTimeOffset instant, Func<DateOnly, TimeSpan> offsetFor)
    {
        ArgumentNullException.ThrowIfNull(offsetFor);
        // Which Cairo date is it at that instant? Take the offset for the instant's own date first, then read
        // the local date off it — using UTC's date here is what produced the two-hour error.
        var probe = offsetFor(DateOnly.FromDateTime(instant.UtcDateTime));
        var localDate = DateOnly.FromDateTime(instant.ToOffset(probe).DateTime);

        // Re-resolve the offset for that local date so a day that straddles a DST change uses its own.
        var dayOffset = offsetFor(localDate);
        var start = new DateTimeOffset(localDate.ToDateTime(TimeOnly.MinValue), dayOffset);

        var next = localDate.AddDays(1);
        var end = new DateTimeOffset(next.ToDateTime(TimeOnly.MinValue), offsetFor(next));
        return (start, end);
    }
}
