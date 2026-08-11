using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

/// <summary>
/// The licence impact preview (design 42 §3) — what shortening an expiry would strand.
///
/// <para>The roster has made an impact preview mandatory since 25.4, because closing a clinic day without
/// seeing whose day it is, is how eight people travel to a locked building. Bringing a licence expiry forward
/// does the same thing by a different route — a doctor whose expiry moves from December to September cannot
/// lawfully see anyone in October or November — and the licence screen applied it with no preview at all.</para>
///
/// <para>The boundary is the assertion that matters. INCLUSIVE of the expiry date, matching
/// <c>PractitionerLicence.IsValidAt</c> and <c>SlotGeneration.BookableUntil</c>: a doctor is not unlicensed on
/// the last day printed on their own certificate. An off-by-one here puts that day's patients on a list
/// telling a coordinator to ring them for nothing.</para>
/// </summary>
public class LicenceImpactTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task Only_appointments_AFTER_the_proposed_expiry_are_listed()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            var doctorId = Guid.NewGuid();
            var expiry = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(30));

            // One appointment before the expiry, one ON it, one after.
            var before = await SeedAppointmentAsync(app, doctorId, expiry.AddDays(-5));
            var onTheDay = await SeedAppointmentAsync(app, doctorId, expiry);
            var after = await SeedAppointmentAsync(app, doctorId, expiry.AddDays(1));
            var wellAfter = await SeedAppointmentAsync(app, doctorId, expiry.AddDays(40));

            using var client = SupervisorClient(app);
            var res = await client.GetAsync(
                $"/api/v1/appointments/licence-impact?doctorId={doctorId}&expiry={expiry:yyyy-MM-dd}");
            res.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await res.Content.ReadAsStringAsync());

            var body = await res.Content.ReadFromJsonAsync<JsonElement>(Web);
            var ids = body.GetProperty("affected").EnumerateArray()
                .Select(a => a.GetProperty("appointmentId").GetGuid()).ToList();

            ids.Should().Contain([after, wellAfter]);
            ids.Should().NotContain(before);
            ids.Should().NotContain(onTheDay,
                "the licence covers the day printed on it — flagging that day's patients would send a " +
                "coordinator to ring people whose appointment is perfectly lawful");
            body.GetProperty("affectedCount").GetInt32().Should().Be(ids.Count);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_cancelled_appointment_needs_no_reassigning()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            var doctorId = Guid.NewGuid();
            var expiry = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(10));

            var live = await SeedAppointmentAsync(app, doctorId, expiry.AddDays(7));
            await SeedAppointmentAsync(app, doctorId, expiry.AddDays(8), AppointmentStatus.Cancelled);

            using var client = SupervisorClient(app);
            var body = await (await client.GetAsync(
                $"/api/v1/appointments/licence-impact?doctorId={doctorId}&expiry={expiry:yyyy-MM-dd}"))
                .Content.ReadFromJsonAsync<JsonElement>(Web);

            var ids = body.GetProperty("affected").EnumerateArray()
                .Select(a => a.GetProperty("appointmentId").GetGuid()).ToList();

            ids.Should().ContainSingle().Which.Should().Be(live);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_expiry_far_enough_out_strands_nobody()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            var doctorId = Guid.NewGuid();
            await SeedAppointmentAsync(app, doctorId, DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(20)));

            using var client = SupervisorClient(app);
            var expiry = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(400));
            var body = await (await client.GetAsync(
                $"/api/v1/appointments/licence-impact?doctorId={doctorId}&expiry={expiry:yyyy-MM-dd}"))
                .Content.ReadFromJsonAsync<JsonElement>(Web);

            // The renewal case, and the common one. It has to be as clearly answered as the alarming one:
            // "0 affected" is what lets a coordinator save without hesitating.
            body.GetProperty("affectedCount").GetInt32().Should().Be(0);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// A MEMBER-scoped caller, so the branch predicate does not narrow this test.
    ///
    /// <para>Not an evasion — a separation of concerns. <c>EmrApiFactory</c>'s branch directory grants nothing,
    /// so a branch-scoped caller correctly falls to the <c>NoBranchSentinel</c> and reads zero rows: the
    /// fail-closed behaviour, working. Proving it again here would test <see cref="Mersal.Authz.BranchQueryScope"/>,
    /// which has its own suite. What is under test on this endpoint is the DATE BOUNDARY, and it is the thing
    /// no other test covers.</para>
    /// </summary>
    private static HttpClient SupervisorClient(EmrApiFactory app) =>
        app.As("u-case-manager", "case_manager", "appointment:read");

    private static async Task<Guid> SeedAppointmentAsync(
        EmrApiFactory app, Guid doctorId, DateOnly on, AppointmentStatus status = AppointmentStatus.Booked)
    {
        // Mid-morning Cairo, normalized to UTC (Npgsql refuses a non-zero offset on timestamptz).
        var start = new DateTimeOffset(on.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(2)).ToUniversalTime();

        await using var db = EmrApiFactory.Ctx();
        var appt = new Appointment
        {
            AppointmentId = Guid.NewGuid(), TenantId = app.Tenant, BeneficiaryId = Guid.NewGuid(),
            ProviderId = Guid.NewGuid(), LocationId = Guid.NewGuid(), DoctorId = doctorId,
            AppointmentType = AppointmentType.Scheduled, Status = status,
            ScheduledStart = start, ScheduledEnd = start.AddMinutes(15),
            BeneficiaryName = "Test Patient",
            CreatedAt = start, UpdatedAt = start,
        };
        db.Appointments.Add(appt);
        await db.SaveChangesAsync();
        return appt.AppointmentId;
    }
}
