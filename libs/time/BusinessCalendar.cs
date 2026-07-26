using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mersal.Time;

/// <summary>
/// Phase 18.A3 — the platform's answer to "what day is it?".
///
/// Timestamps are stored as <c>timestamptz</c> in UTC (root CLAUDE.md), but every BUSINESS date decision
/// is an Egyptian calendar date: whether a coverage is in effect, which reset period a limit sits in, a
/// claim's service date, an appointment's day. Using <c>DateOnly.FromDateTime(DateTime.UtcNow)</c> for
/// those is wrong for two to three hours every single evening — at 23:30 in Cairo the UTC date is still
/// yesterday, so a coverage that expired yesterday still looks valid and a limit reset lands a day late.
///
/// <see cref="Today"/> is the only correct source for a business date. Anything that needs an instant
/// keeps using the injected <see cref="TimeProvider"/>; nothing in production code may call
/// <c>DateTimeOffset.UtcNow</c> directly (enforced by the architecture test in libs/time/Tests).
/// </summary>
public interface IBusinessCalendar
{
    /// <summary>The current business date in the platform's operating time zone (Africa/Cairo).</summary>
    DateOnly Today();

    /// <summary>The business date an instant falls on, for stamping a date onto a recorded event.</summary>
    DateOnly DateOf(DateTimeOffset instant);

    /// <summary>The platform's operating time zone. Exposed so display formatting agrees with the
    /// business date rather than re-deriving the zone from a string in another layer.</summary>
    TimeZoneInfo Zone { get; }
}

/// <summary>Africa/Cairo business calendar over an injected <see cref="TimeProvider"/>, so tests can
/// pin the clock and assert boundary behaviour (23:30 Cairo evaluates against the Cairo date).</summary>
public sealed class BusinessCalendar(TimeProvider clock) : IBusinessCalendar
{
    /// <summary>IANA id first; Windows falls back to its own id so the library is portable.</summary>
    public static TimeZoneInfo CairoZone { get; } = Resolve();

    public TimeZoneInfo Zone => CairoZone;

    public DateOnly Today() => DateOf(clock.GetUtcNow());

    public DateOnly DateOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, CairoZone).DateTime);

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "Africa/Cairo", "Egypt Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        // Last resort: Egypt is UTC+2 (UTC+3 during DST). A fixed offset keeps the platform running on a
        // host with no tz database rather than failing every date decision.
        return TimeZoneInfo.CreateCustomTimeZone("Mersal/Cairo", TimeSpan.FromHours(2), "Mersal Cairo", "Mersal Cairo");
    }
}

public static class BusinessCalendarServiceCollectionExtensions
{
    /// <summary>Register the system clock + the Africa/Cairo business calendar. Idempotent, so a service
    /// that already registered <see cref="TimeProvider"/> keeps its registration (tests substitute a
    /// fake clock before calling this).</summary>
    public static IServiceCollection AddHbmpBusinessCalendar(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IBusinessCalendar, BusinessCalendar>();
        return services;
    }
}
