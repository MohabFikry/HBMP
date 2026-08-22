using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Emr.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Tests;

/// <summary>
/// <c>GET /roster/day</c> (33.10) — one clinic, one day, over HTTP.
/// </summary>
/// <remarks>
/// <para>What these pin is not arithmetic but AUTHORSHIP. The weekly pattern and the exception calendar are
/// two screens, and the answer a coordinator actually wants — is this clinician in on Thursday, and how many
/// can they still take — is neither of them. It is the two combined under four rules that are easy to state
/// and easy to get wrong: a whole-day closure beats an extra clinic, a part-day absence shortens a session
/// without cancelling it, the daily cap applies across every window the date offers and after subtraction,
/// and a trailing partial slot is not a slot.</para>
///
/// <para>Those rules live in <see cref="SlotGeneration"/> and are already tested there. The risk this suite
/// covers is that the day view answers them a SECOND time — which is why the assertions below are about the
/// endpoint agreeing with the generator on cases where a plausible re-implementation would not: the line that
/// survives as <c>Off</c> rather than disappearing, the part-day cut that is not a closure, and the extra
/// clinic that a closure outranks.</para>
/// </remarks>
[Collection("emr-db")]
public class DayRosterTests
{
    private static readonly Guid Maadi = Guid.Parse("aa000000-0000-4000-8000-00000000000d");
    private static readonly Guid Dokki = Guid.Parse("aa000000-0000-4000-8000-00000000000e");
    private static readonly Guid Hala = Guid.Parse("aa000000-0000-4000-8000-0000000000aa");

    /// <summary>Tuesday 8 September 2026. A named weekday, because the endpoint selects rules by it.</summary>
    private static readonly DateOnly Tuesday = new(2026, 9, 8);
    private static readonly DateOnly Wednesday = new(2026, 9, 9);

    // ── the happy day ────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_working_day_reports_the_pattern_the_cap_and_what_is_already_booked()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory { HomeBranch = Maadi };
        try
        {
            await SeedRuleAsync(app, DayOfWeek.Tuesday, "09:00", "13:00", slotMinutes: 15, maxPerDay: 12);
            await SeedAppointmentsAsync(app, Maadi, 3);

            var body = await DayAsync(app, Tuesday);
            var line = body.GetProperty("lines").EnumerateArray().Single();

            line.GetProperty("status").GetString().Should().Be("Working");
            // 16 slots fit in four hours at fifteen minutes; the clinician takes 12. BOTH numbers, because
            // "12 of 16" is the sentence the coordinator is reading and either alone misleads.
            line.GetProperty("slotsFromPattern").GetInt32().Should().Be(12);
            line.GetProperty("slotsOffered").GetInt32().Should().Be(12);
            line.GetProperty("booked").GetInt32().Should().Be(3);

            var summary = body.GetProperty("summary");
            summary.GetProperty("clinicians").GetInt32().Should().Be(1);
            summary.GetProperty("open").GetInt32().Should().Be(9);
        }
        finally { await CleanupAsync(app); }
    }

    /// <summary>
    /// A day the pattern does not cover has no line for it — the endpoint reads the weekday, it does not
    /// return the whole week and leave the client to filter.
    /// </summary>
    [SkippableFact]
    public async Task A_weekday_the_pattern_does_not_cover_has_no_line()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory { HomeBranch = Maadi };
        try
        {
            await SeedRuleAsync(app, DayOfWeek.Tuesday, "09:00", "13:00", slotMinutes: 15, maxPerDay: 12);

            var body = await DayAsync(app, Wednesday);
            body.GetProperty("lines").GetArrayLength().Should().Be(0);
        }
        finally { await CleanupAsync(app); }
    }

    // ── absence ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Leave keeps the line and reports it as Off, with the reason attached.
    /// </summary>
    /// <remarks>
    /// The tempting implementation drops a clinician with no slots, and it is wrong in the way that matters:
    /// "Dr Hala is not on today's roster" and "Dr Hala is on annual leave" are the same screen to a
    /// coordinator ringing round for cover, and only one of them tells them what to do next.
    /// </remarks>
    [SkippableFact]
    public async Task Leave_leaves_the_line_in_place_and_names_the_reason()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory { HomeBranch = Maadi };
        try
        {
            await SeedRuleAsync(app, DayOfWeek.Tuesday, "09:00", "13:00", slotMinutes: 15, maxPerDay: 12);
            await SeedExceptionAsync(app, RosterExceptionKind.Leave, Tuesday, practitioner: Hala,
                reason: "Annual leave");

            var line = (await DayAsync(app, Tuesday)).GetProperty("lines").EnumerateArray().Single();

            line.GetProperty("status").GetString().Should().Be("Off");
            line.GetProperty("slotsOffered").GetInt32().Should().Be(0);
            line.GetProperty("exceptionReason").GetString().Should().Be("Annual leave");
            // The pattern is INTACT. What the day lost is not what the week says.
            line.GetProperty("slotsFromPattern").GetInt32().Should().Be(12);
        }
        finally { await CleanupAsync(app); }
    }

    /// <summary>
    /// A part-day absence shortens the session; it does not close it.
    /// </summary>
    [SkippableFact]
    public async Task An_afternoon_away_shortens_the_day_and_still_reads_as_working()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory { HomeBranch = Maadi };
        try
        {
            await SeedRuleAsync(app, DayOfWeek.Tuesday, "09:00", "13:00", slotMinutes: 15, maxPerDay: 12);
            await SeedExceptionAsync(app, RosterExceptionKind.Leave, Tuesday, practitioner: Hala,
                reason: "Hospital round", start: "11:00", end: "13:00");

            var line = (await DayAsync(app, Tuesday)).GetProperty("lines").EnumerateArray().Single();

            line.GetProperty("status").GetString().Should().Be("Working");
            // Two hours of fifteen-minute slots survive. The cap of 12 never binds, because subtraction
            // happens first — the order SlotGeneration documents and this proves the endpoint inherits.
            line.GetProperty("slotsOffered").GetInt32().Should().Be(8);
            line.GetProperty("exceptionReason").GetString().Should().Be("Hospital round");
        }
        finally { await CleanupAsync(app); }
    }

    /// <summary>
    /// A closure on a day nobody is rostered produces no lines — and still explains itself.
    /// </summary>
    [SkippableFact]
    public async Task A_closure_with_nothing_to_close_is_still_reported_as_a_notice()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory { HomeBranch = Maadi };
        try
        {
            await SeedRuleAsync(app, DayOfWeek.Tuesday, "09:00", "13:00", slotMinutes: 15, maxPerDay: 12);
            await SeedExceptionAsync(app, RosterExceptionKind.PublicHoliday, Wednesday, branch: Maadi,
                reason: "Eid al-Adha");

            var body = await DayAsync(app, Wednesday);

            body.GetProperty("lines").GetArrayLength().Should().Be(0);
            // Without this the screen says "nobody is working today" for a bank holiday and for a rota
            // somebody forgot to enter, in identical words.
            body.GetProperty("notices").EnumerateArray().Single()
                .GetProperty("reason").GetString().Should().Be("Eid al-Adha");
        }
        finally { await CleanupAsync(app); }
    }

    // ── extra clinics ────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task An_extra_clinic_on_an_uncovered_day_gets_its_own_line()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory { HomeBranch = Maadi };
        try
        {
            await SeedRuleAsync(app, DayOfWeek.Tuesday, "09:00", "13:00", slotMinutes: 15, maxPerDay: null);
            await SeedExceptionAsync(app, RosterExceptionKind.AdHocClinic, Wednesday, branch: Maadi,
                practitioner: Hala, reason: "Catch-up clinic", start: "14:00", end: "17:00");

            var line = (await DayAsync(app, Wednesday)).GetProperty("lines").EnumerateArray().Single();

            line.GetProperty("status").GetString().Should().Be("Extra");
            line.GetProperty("startTime").GetString().Should().Be("14:00");
            // Three hours cut into the fifteen-minute slots this clinician's own pattern uses — the length an
            // ad-hoc row does not carry and the generator borrows the same way.
            line.GetProperty("slotsOffered").GetInt32().Should().Be(12);
        }
        finally { await CleanupAsync(app); }
    }

    /// <summary>
    /// A whole-day closure outranks an extra clinic, exactly as it does in the generator.
    /// </summary>
    /// <remarks>
    /// The other ordering is what a re-implementation reaches for, and it lets a stale ad-hoc row quietly
    /// reopen a branch somebody closed.
    /// </remarks>
    [SkippableFact]
    public async Task A_closed_clinic_outranks_an_extra_session_on_the_same_day()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory { HomeBranch = Maadi };
        try
        {
            await SeedRuleAsync(app, DayOfWeek.Tuesday, "09:00", "13:00", slotMinutes: 15, maxPerDay: null);
            await SeedExceptionAsync(app, RosterExceptionKind.AdHocClinic, Wednesday, branch: Maadi,
                practitioner: Hala, reason: "Catch-up clinic", start: "14:00", end: "17:00");
            await SeedExceptionAsync(app, RosterExceptionKind.ClinicClosed, Wednesday, branch: Maadi,
                reason: "Power cut");

            var body = await DayAsync(app, Wednesday);

            body.GetProperty("lines").GetArrayLength().Should().Be(0);
            body.GetProperty("notices").GetArrayLength().Should().Be(2);
        }
        finally { await CleanupAsync(app); }
    }

    // ── scope ────────────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Another_clinics_roster_is_not_on_this_desks_day()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory { HomeBranch = Maadi };
        try
        {
            await SeedRuleAsync(app, DayOfWeek.Tuesday, "09:00", "13:00", slotMinutes: 15, maxPerDay: 12);
            await SeedRuleAsync(app, DayOfWeek.Tuesday, "09:00", "17:00", slotMinutes: 15, maxPerDay: null,
                branch: Dokki, doctor: Guid.NewGuid());
            await SeedAppointmentsAsync(app, Dokki, 5);

            var body = await DayAsync(app, Tuesday);

            body.GetProperty("lines").GetArrayLength().Should().Be(1);
            body.GetProperty("summary").GetProperty("booked").GetInt32().Should().Be(0);
        }
        finally { await CleanupAsync(app); }
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<JsonElement> DayAsync(EmrApiFactory app, DateOnly date)
    {
        using var desk = app.ReceptionClient();
        var r = await desk.GetAsync(new Uri($"/api/v1/roster/day?date={date:yyyy-MM-dd}", UriKind.Relative));
        r.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await r.Content.ReadAsStringAsync());
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task SeedRuleAsync(
        EmrApiFactory app, DayOfWeek day, string start, string end, int slotMinutes, int? maxPerDay,
        Guid? branch = null, Guid? doctor = null)
    {
        await using var db = EmrApiFactory.Ctx();
        db.ProviderAvailabilities.Add(new ProviderAvailability
        {
            AvailabilityId = Guid.NewGuid(), TenantId = app.Tenant,
            ProviderId = Guid.NewGuid(), LocationId = Guid.NewGuid(),
            BranchId = branch ?? Maadi, DoctorId = doctor ?? Hala, DayOfWeek = day,
            StartTime = TimeOnly.Parse(start, null), EndTime = TimeOnly.Parse(end, null),
            SlotMinutes = slotMinutes, MaxPerDay = maxPerDay,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedExceptionAsync(
        EmrApiFactory app, RosterExceptionKind kind, DateOnly date, string reason,
        Guid? branch = null, Guid? practitioner = null, string? start = null, string? end = null)
    {
        await using var db = EmrApiFactory.Ctx();
        db.RosterExceptions.Add(new RosterException
        {
            ExceptionId = Guid.NewGuid(), TenantId = app.Tenant,
            BranchId = branch, PractitionerId = practitioner,
            DateFrom = date, DateTo = date, Kind = kind, Reason = reason,
            StartTime = start is null ? null : TimeOnly.Parse(start, null),
            EndTime = end is null ? null : TimeOnly.Parse(end, null),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Bookings at 10:00 Cairo, comfortably inside the pattern's window at either offset.</summary>
    private static async Task SeedAppointmentsAsync(EmrApiFactory app, Guid branch, int count)
    {
        await using var db = EmrApiFactory.Ctx();
        for (var i = 0; i < count; i++)
        {
            var start = new DateTimeOffset(Tuesday.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(3))
                .AddMinutes(15 * i);
            db.Appointments.Add(new Appointment
            {
                AppointmentId = Guid.NewGuid(), TenantId = app.Tenant,
                BeneficiaryId = Guid.NewGuid(), ProviderId = Guid.NewGuid(), LocationId = Guid.NewGuid(),
                BranchId = branch, DoctorId = Hala,
                AppointmentType = AppointmentType.Scheduled, Status = AppointmentStatus.Booked,
                ScheduledStart = start.ToUniversalTime(), ScheduledEnd = start.AddMinutes(15).ToUniversalTime(),
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>The factory's sweep does not reach roster exceptions — nothing else in this project writes
    /// them — so this suite clears its own.</summary>
    private static async Task CleanupAsync(EmrApiFactory app)
    {
        await using (var db = EmrApiFactory.Ctx())
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM emr.roster_exception_history WHERE tenant_id = {0}; " +
                "DELETE FROM emr.roster_exception WHERE tenant_id = {0};", app.Tenant);
        }
        await app.CleanupAsync();
    }
}
