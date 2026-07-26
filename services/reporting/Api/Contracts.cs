using System.Globalization;
using Mersal.Time;

namespace Mersal.Reporting.Api;

/// <summary>The projection seam payload (system → reporting-service): a canonical domain event with a min-necessary,
/// de-identified field bag. No PHI crosses this boundary.</summary>
public sealed record ProjectionRequest(
    Guid EventId,
    string EventType,
    string TenantId,
    Dictionary<string, string> Fields,
    DateTimeOffset? OccurredAt);

/// <summary>An async job handle returned for heavy / long-range analytics (NFR-006).</summary>
public sealed record JobHandleView(Guid JobId, string Status, int ProgressPercent, string? PollUrl);

/// <summary>Parses the shared period-range query params, defaulting to the trailing 30 days, and flags a long range
/// (heavy → async).</summary>
public static class Period
{
    public const int LongRangeDays = 92;

    public static (DateOnly From, DateOnly To) Parse(string? from, string? to, IBusinessCalendar calendar)
    {
        var today = calendar.Today();   // 18.A3
        var t = TryDate(to) ?? today;
        var f = TryDate(from) ?? t.AddDays(-30);
        return (f, t);
    }

    public static bool IsLongRange(DateOnly from, DateOnly to) => to.DayNumber - from.DayNumber > LongRangeDays;

    private static DateOnly? TryDate(string? s) =>
        DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
