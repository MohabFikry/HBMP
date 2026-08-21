using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

/// <summary>
/// <c>GET /appointments/summary</c> — the reception dashboard's cards, over HTTP.
/// </summary>
/// <remarks>
/// <para>The endpoint has served the dashboard since 14.5 and had no test of any kind. What that left
/// unexamined is not the arithmetic but the SHAPE: it reported Total, CheckedIn and NoShow, so the one status
/// a desk can act on — Cancelled, the appointments that freed their slot — was counted into <c>total</c> and
/// then never named. A morning with eleven cancellations and a morning with none read identically.</para>
///
/// <para><c>total</c> is deliberately NOT the sum of the named states, and that is pinned below rather than
/// left to be discovered: Booked and Completed are none of them, and a cancelled appointment stays counted in
/// the book it was struck from. A future change that "fixes" the summary by making the figures add up would
/// be changing what <c>total</c> means.</para>
/// </remarks>
[Collection("emr-db")]
public class AppointmentSummaryTests
{
    /// <summary>Mid-morning UTC, which is comfortably inside the same Cairo civil day at either offset — so
    /// the window the endpoint computes and the instants this seeds cannot land on different days.</summary>
    private static readonly DateTimeOffset Midday = new(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid Dokki = Guid.Parse("11111111-2222-4333-8444-555555555555");

    [SkippableFact]
    public async Task Cancellations_are_counted_and_named_rather_than_folded_into_the_total()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory { HomeBranch = Dokki };
        try
        {
            // A plausible morning: nine on the book, of which two have arrived, one never did, three rang
            // ahead, and the rest are still expected.
            await SeedAsync(app,
                (AppointmentStatus.CheckedIn, 2),
                (AppointmentStatus.NoShow, 1),
                (AppointmentStatus.Cancelled, 3),
                (AppointmentStatus.Booked, 2),
                (AppointmentStatus.Completed, 1));

            using var reception = app.ReceptionClient();
            var r = await reception.GetAsync(new Uri(
                $"/api/v1/appointments/summary?date={Uri.EscapeDataString(Midday.ToString("O"))}", UriKind.Relative));

            r.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await r.Content.ReadAsStringAsync());
            var body = await r.Content.ReadFromJsonAsync<JsonElement>();

            body.GetProperty("checkedIn").GetInt32().Should().Be(2);
            body.GetProperty("noShow").GetInt32().Should().Be(1);
            // THE assertion. Three cancellations used to be visible only as part of `total`, which is to say
            // not visible: the desk could not tell them from the four appointments still to arrive.
            body.GetProperty("cancelled").GetInt32().Should().Be(3);

            // The whole book, cancellations included — 2 + 1 + 3 is 6 and this is 9, because Booked and
            // Completed are none of the three named states.
            body.GetProperty("total").GetInt32().Should().Be(9);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// Cancelled and NoShow are read from the status column, not from one another.
    /// </summary>
    /// <remarks>
    /// Both are appointments nobody attended, which is what makes them easy to conflate and what makes
    /// conflating them costly: a cancellation frees its slot and can promote somebody off the waitlist, and a
    /// no-show consumes a slot nobody could reuse. A day made entirely of one must not report any of the
    /// other.
    /// </remarks>
    [SkippableFact]
    public async Task A_day_of_cancellations_reports_no_no_shows()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory { HomeBranch = Dokki };
        try
        {
            await SeedAsync(app, (AppointmentStatus.Cancelled, 4));

            using var reception = app.ReceptionClient();
            var r = await reception.GetAsync(new Uri(
                $"/api/v1/appointments/summary?date={Uri.EscapeDataString(Midday.ToString("O"))}", UriKind.Relative));
            var body = await r.Content.ReadFromJsonAsync<JsonElement>();

            body.GetProperty("cancelled").GetInt32().Should().Be(4);
            body.GetProperty("noShow").GetInt32().Should().Be(0);
            body.GetProperty("checkedIn").GetInt32().Should().Be(0);
            body.GetProperty("total").GetInt32().Should().Be(4);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// A card that counted another branch's cancellations would be a scoping hole dressed as a statistic —
    /// the endpoint's own words. The new figure goes through the same <c>ApplyBranchScope</c> as the three it
    /// joins, and this is what proves it rather than assuming it.
    /// </summary>
    [SkippableFact]
    public async Task Another_branchs_cancellations_are_not_on_this_desks_card()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory { HomeBranch = Dokki };
        try
        {
            await SeedAsync(app, (AppointmentStatus.Cancelled, 1));
            await SeedAsync(app, branch: Guid.NewGuid(), (AppointmentStatus.Cancelled, 5));

            using var reception = app.ReceptionClient();
            var r = await reception.GetAsync(new Uri(
                $"/api/v1/appointments/summary?date={Uri.EscapeDataString(Midday.ToString("O"))}", UriKind.Relative));
            var body = await r.Content.ReadFromJsonAsync<JsonElement>();

            body.GetProperty("cancelled").GetInt32().Should().Be(1);
            body.GetProperty("total").GetInt32().Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    private static Task SeedAsync(EmrApiFactory app, params (AppointmentStatus Status, int Count)[] by) =>
        SeedAsync(app, Dokki, by);

    private static async Task SeedAsync(EmrApiFactory app, Guid branch, params (AppointmentStatus Status, int Count)[] by)
    {
        await using var db = EmrApiFactory.Ctx();
        var slot = 0;
        foreach (var (status, count) in by)
        {
            for (var i = 0; i < count; i++)
            {
                var start = Midday.AddMinutes(15 * slot++);
                db.Appointments.Add(new Appointment
                {
                    AppointmentId = Guid.NewGuid(),
                    // ck_appointment_tenant_not_blank — and a row in another tenant is invisible to this
                    // caller anyway, which is not the scoping this suite means to exercise.
                    TenantId = app.Tenant,
                    BeneficiaryId = Guid.NewGuid(), ProviderId = Guid.NewGuid(), LocationId = Guid.NewGuid(),
                    BranchId = branch, AppointmentType = AppointmentType.Scheduled, Status = status,
                    ScheduledStart = start, ScheduledEnd = start.AddMinutes(15),
                    CreatedAt = Midday, UpdatedAt = Midday,
                });
            }
        }
        await db.SaveChangesAsync();
    }
}
