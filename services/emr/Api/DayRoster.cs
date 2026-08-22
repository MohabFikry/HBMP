using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Api;

/// <summary>
/// 33.10 (design 42 §4/§6) — <b>one clinic, one day</b>: who is actually working, in what hours, and how
/// full they are.
///
/// <para><b>The question the weekly pattern cannot answer.</b> The pattern says what normally happens; the
/// exceptions say what does not. Reading a roster off the two by eye means holding four rules in your head at
/// once — a whole-day closure beats an extra clinic, a part-day leave shortens a session without cancelling
/// it, the daily cap applies across every window the date offers and AFTER subtraction, and a trailing
/// partial slot is not a slot. A coordinator asking "is Dr Hala in on Thursday, and how many can she still
/// take" was being asked to run that algorithm themselves.</para>
///
/// <para><b>So it is computed where it is already implemented.</b> Every line here comes out of
/// <see cref="SlotGeneration.Generate"/> — the one place availability is decided (design 42 §7 rule 5) — run
/// for a single date. Deriving the same answer in the browser would be a second implementation of the four
/// rules above, in a language with no tests over them, and the first divergence would be a clinic telling a
/// patient it was open on a day the booking engine had already closed.</para>
///
/// <para><b>Read-only, at <c>appointment:read</c>.</b> Same reasoning as the pattern itself: the desk needs
/// to know when the clinic runs, and only the people who run it may change that.</para>
/// </summary>
public static class DayRosterEndpoints
{
    /// <summary>How the line came to be. Named by the SERVER so no client re-derives it from a slot count.</summary>
    public const string Working = "Working";
    /// <summary>The pattern says they work and an exception removed the whole day.</summary>
    public const string Off = "Off";
    /// <summary>An ad-hoc clinic on a date the weekly pattern does not cover at all.</summary>
    public const string Extra = "Extra";

    public static void MapDayRoster(this WebApplication app)
    {
        var read = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("appointment:read"));

        // GET /roster/day?branchId=&date= — the clinic's day.
        //
        // `branchId` is OPTIONAL rather than required, even though the screen always sends one. A branch-scoped
        // coordinator has exactly one clinic and would have to name it to read their own roster; the scope
        // helper already narrows them to it, and demanding the parameter would be asking a question whose
        // answer the server holds.
        read.MapGet("/roster/day", async (
            Guid? branchId, DateOnly? date,
            BranchScopeState branch, EmrDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var day = date ?? ClinicToday(clock);
            var offset = CairoOffsetOn(day);

            // Every rule in reach, not only those whose weekday matches. A rule for another weekday is what
            // tells us the slot length an ad-hoc clinic on this date would be cut into — see AdHocLines.
            var rules = await db.ProviderAvailabilities.AsNoTracking()
                .ApplyBranchScope(a => a.BranchId, branch.Mode, branch.Context, branchId)
                .Take(2000).ToListAsync(ct);

            var permitted = BranchQueryScope.PermittedFor(branch.Mode, branch.Context, branchId);
            var exceptionsQuery = db.RosterExceptions.AsNoTracking()
                .Where(e => e.DateFrom <= day && e.DateTo >= day);
            if (permitted is not null)
            {
                var ids = permitted.ToList();
                // A practitioner-only exception (branch null) belongs to no single clinic and must still be
                // seen — "Dr Hala is away" is exactly the row the clinic she was due to work at needs.
                exceptionsQuery = exceptionsQuery.Where(e => e.BranchId == null || ids.Contains(e.BranchId.Value));
            }
            var exceptions = await exceptionsQuery.Take(1000).ToListAsync(ct);

            // Booked appointments for the Cairo civil day. Converted here rather than compared as UTC dates,
            // for the reason the impact query gives: a UTC day clips the first hours of the clinic's morning.
            var lo = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), offset).ToUniversalTime();
            var hi = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), CairoOffsetOn(day.AddDays(1))).ToUniversalTime();
            var bookedRows = await db.Appointments.AsNoTracking()
                .ApplyBranchScope(a => a.BranchId, branch.Mode, branch.Context, branchId)
                .Where(a => a.ScheduledStart >= lo && a.ScheduledStart < hi
                            // CANCELLED only. A no-show consumed the slot and a completed visit used it; both
                            // are load the clinician carried, and a roster that hid them would report a full
                            // afternoon as free.
                            && a.Status != AppointmentStatus.Cancelled)
                .Select(a => new { a.DoctorId, a.BranchId })
                .Take(5000).ToListAsync(ct);

            var booked = bookedRows
                .GroupBy(a => (a.DoctorId, a.BranchId))
                .ToDictionary(g => g.Key, g => g.Count());

            var lines = new List<DayRosterLine>();

            // ── The pattern's own lines ────────────────────────────────────────────────────────────────────
            foreach (var rule in rules.Where(r => r.DayOfWeek == day.DayOfWeek))
            {
                var applicable = exceptions.Where(e => e.AppliesTo(day, rule.BranchId, rule.DoctorId)).ToList();
                var slots = SlotGeneration.Generate(rule, day, day, offset, bookableUntil: null, exceptions: applicable);
                var blocking = applicable.FirstOrDefault(e => e.IsSubtractive);

                lines.Add(new DayRosterLine(
                    rule.AvailabilityId, rule.DoctorId, rule.BranchId,
                    rule.StartTime.ToString("HH\\:mm"), rule.EndTime.ToString("HH\\:mm"),
                    rule.SlotMinutes, rule.MaxPerDay,
                    SlotGeneration.EffectiveSlotsPerDay(rule.StartTime, rule.EndTime, rule.SlotMinutes, rule.MaxPerDay),
                    slots.Count,
                    booked.GetValueOrDefault((rule.DoctorId, rule.BranchId)),
                    slots.Count == 0 && blocking is not null ? Off : Working,
                    blocking?.Kind.ToString(), blocking?.Reason));
            }

            lines.AddRange(AdHocLines(day, rules, exceptions, booked));

            // Ordered by the clinic's clock, then by the id, so two clinicians opening at nine keep a stable
            // order between reloads rather than swapping places as the query planner sees fit.
            lines = [.. lines.OrderBy(l => l.StartTime, StringComparer.Ordinal).ThenBy(l => l.PractitionerId)];

            var offered = lines.Sum(l => l.SlotsOffered);
            var taken = lines.Sum(l => l.Booked);

            return Results.Ok(new DayRosterResponse(
                day, branchId, lines,
                [.. exceptions.Select(ToNotice)],
                new DayRosterSummary(
                    lines.Count(l => l.Status != Off),
                    offered, taken,
                    // Floored at zero. Overbooking is possible — a walk-in is booked without consuming a slot
                    // — and "-2 open" is not a number anyone can act on.
                    Math.Max(0, offered - taken))));
        })
        // Named on the route so the generated spec describes the shape rather than an untyped 200. The
        // response-schema gate counts these, and a day roster is exactly the kind of read a client would
        // otherwise have to learn by calling it.
        .Produces<DayRosterResponse>();
    }

    /// <summary>
    /// The EXTRA sessions: an ad-hoc clinic on a date this clinician has no weekly pattern for.
    ///
    /// <para>Ad-hoc windows that land on a day the pattern DOES cover are already folded into that line by
    /// <see cref="SlotGeneration.Generate"/>, which adds them as a second window under the same daily cap.
    /// Emitting them again here would double-count the extra afternoon on exactly the day somebody added
    /// capacity.</para>
    ///
    /// <para><b>Slot length is borrowed from the clinician's other rules, and it can be missing.</b> An
    /// ad-hoc exception carries hours and no slot length, because in the generator the length comes from
    /// whichever availability rule the window is generated against. A clinician with no rule at this clinic
    /// therefore generates nothing from an ad-hoc row — the line is reported with zero slots and its reason
    /// attached, which is the true answer rather than a blank.</para>
    /// </summary>
    private static IEnumerable<DayRosterLine> AdHocLines(
        DateOnly day, List<ProviderAvailability> rules, List<RosterException> exceptions,
        Dictionary<(Guid? DoctorId, Guid? BranchId), int> booked)
    {
        foreach (var adHoc in exceptions.Where(e =>
                     e.Kind == RosterExceptionKind.AdHocClinic && e.AppliesTo(day, e.BranchId, e.PractitionerId)))
        {
            // A whole-day closure beats an extra clinic — the same precedence the generator applies, and for
            // the same reason: an extra session at a shut clinic is not a session.
            if (exceptions.Any(e => e.IsSubtractive && e.IsWholeDay
                                    && e.AppliesTo(day, adHoc.BranchId, adHoc.PractitionerId)))
                continue;

            var covered = rules.Any(r => r.DayOfWeek == day.DayOfWeek
                                         && r.DoctorId == adHoc.PractitionerId && r.BranchId == adHoc.BranchId);
            if (covered) continue;

            var template = rules.FirstOrDefault(r => r.DoctorId == adHoc.PractitionerId && r.BranchId == adHoc.BranchId);
            var minutes = template?.SlotMinutes ?? 0;
            var start = adHoc.StartTime ?? TimeOnly.MinValue;
            var end = adHoc.EndTime ?? TimeOnly.MaxValue;

            yield return new DayRosterLine(
                null, adHoc.PractitionerId, adHoc.BranchId,
                start.ToString("HH\\:mm"), end.ToString("HH\\:mm"),
                minutes, template?.MaxPerDay,
                SlotGeneration.EffectiveSlotsPerDay(start, end, minutes, template?.MaxPerDay),
                SlotGeneration.EffectiveSlotsPerDay(start, end, minutes, template?.MaxPerDay),
                booked.GetValueOrDefault((adHoc.PractitionerId, adHoc.BranchId)),
                Extra, adHoc.Kind.ToString(), adHoc.Reason);
        }
    }

    private static DayRosterNotice ToNotice(RosterException e) => new(
        e.ExceptionId, e.Kind.ToString(), e.Reason, e.BranchId, e.PractitionerId,
        e.IsWholeDay, e.StartTime?.ToString("HH\\:mm"), e.EndTime?.ToString("HH\\:mm"), e.IsSubtractive);

    private static DateOnly ClinicToday(TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        // cairo-date: offset-probe (the inner UTC date only selects the offset; the returned date is Cairo's)
        return DateOnly.FromDateTime(now.ToOffset(CairoOffsetOn(DateOnly.FromDateTime(now.UtcDateTime))).DateTime);
    }

    private static TimeSpan CairoOffsetOn(DateOnly on)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Egypt Standard Time" : "Africa/Cairo");
            return tz.GetUtcOffset(on.ToDateTime(TimeOnly.MinValue));
        }
        catch (TimeZoneNotFoundException) { return TimeSpan.FromHours(2); }
        catch (InvalidTimeZoneException) { return TimeSpan.FromHours(2); }
    }
}

/// <param name="SlotsFromPattern">What the weekly pattern alone offers, cap included and exceptions excluded.
/// Returned beside <paramref name="SlotsOffered"/> so "12 of 16, cut short by leave" is one sentence rather
/// than a number the reader has to go and check against another screen.</param>
/// <param name="Booked">Appointments on this clinician's day at this clinic — everything but a cancellation.</param>
public sealed record DayRosterLine(
    Guid? AvailabilityId, Guid? PractitionerId, Guid? BranchId,
    string StartTime, string EndTime, int SlotMinutes, int? MaxPerDay,
    int SlotsFromPattern, int SlotsOffered, int Booked,
    string Status, string? ExceptionKind, string? ExceptionReason);

/// <summary>An exception in force on this date, whether or not any line was changed by it. A clinic closed on
/// a day nobody was rostered has no lines at all, and "why is this day empty" still has an answer.</summary>
public sealed record DayRosterNotice(
    Guid ExceptionId, string Kind, string Reason, Guid? BranchId, Guid? PractitionerId,
    bool WholeDay, string? StartTime, string? EndTime, bool Subtractive);

public sealed record DayRosterSummary(int Clinicians, int SlotsOffered, int Booked, int Open);

public sealed record DayRosterResponse(
    DateOnly Date, Guid? BranchId, IReadOnlyList<DayRosterLine> Lines,
    IReadOnlyList<DayRosterNotice> Notices, DayRosterSummary Summary);
