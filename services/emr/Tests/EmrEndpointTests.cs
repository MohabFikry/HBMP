using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Emr.Api;
using Mersal.Emr.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Tests;

/// <summary>
/// Phase 24 Gate 3 — booking, check-in and the visit gate, over HTTP.
///
/// <para>The appointment state machine and VisitGate are well covered below HTTP. What was not covered is the
/// endpoint layer that decides who reaches them: the practitioner-branch check that refuses to materialize
/// slots for a doctor who does not work at that branch, the visit gate that refuses to open an encounter for
/// a member who is not Active, and the queue ticket a check-in issues — which is a person waiting to be seen,
/// and which one production row had already gone missing from by being written with no tenant.</para>
/// </summary>
[Collection("emr-db")]
public class EmrEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---- slots --------------------------------------------------------------------------------------------

    /// <summary>
    /// FR-BRN-026, the first of the two branch gates and the one the code calls the more important: refusing
    /// here means the bad slots are never materialized, so no patient can be booked into them. Catching it
    /// only at booking time leaves a doctor's calendar full of appointments at a branch they do not work at,
    /// each needing to be cancelled and the patient rung back.
    /// </summary>
    [SkippableFact]
    public async Task Slots_are_not_materialized_for_a_doctor_who_does_not_serve_that_branch()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory { DoctorServesBranch = false };
        try
        {
            using var reception = app.ReceptionClient();
            var r = await reception.PostAsJsonAsync("/api/v1/appointment-slots", Slots(branchId: Guid.NewGuid()), Web);
            r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await r.Content.ReadAsStringAsync()).Should().Contain("practitioner-not-at-branch");

            await using var db = EmrApiFactory.Ctx();
            (await db.AppointmentSlots.CountAsync(s => s.TenantId == app.Tenant)).Should().Be(0);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Materializing_slots_twice_over_the_same_window_creates_no_duplicates()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            using var reception = app.ReceptionClient();
            var req = Slots();

            var first = await reception.PostAsJsonAsync("/api/v1/appointment-slots", req, Web);
            first.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await first.Content.ReadAsStringAsync());
            var created = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("created").GetInt32();
            created.Should().BeGreaterThan(0);

            var second = await reception.PostAsJsonAsync("/api/v1/appointment-slots", req, Web);
            second.StatusCode.Should().Be(HttpStatusCode.OK);
            var repeat = await second.Content.ReadFromJsonAsync<JsonElement>();
            repeat.GetProperty("created").GetInt32().Should().Be(0);
            repeat.GetProperty("skippedExisting").GetInt32().Should().Be(created,
                "materialization is idempotent — re-running the roster must not double the calendar");

            await using var db = EmrApiFactory.Ctx();
            (await db.AppointmentSlots.CountAsync(s => s.ProviderId == req.ProviderId)).Should().Be(created);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_availability_window_that_makes_no_sense_is_refused()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            using var reception = app.ReceptionClient();
            var backwards = Slots() with { EndTime = new TimeOnly(8, 0), StartTime = new TimeOnly(17, 0) };
            (await reception.PostAsJsonAsync("/api/v1/appointment-slots", backwards, Web))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var zeroLength = Slots() with { SlotMinutes = 0 };
            (await reception.PostAsJsonAsync("/api/v1/appointment-slots", zeroLength, Web))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- booking and check-in -----------------------------------------------------------------------------

    /// <summary>The whole reception path: materialize, book, check in. The check-in issues the queue ticket —
    /// the row a clinic's board is drawn from — and it must carry the appointment's tenant, because a ticket
    /// belonging to nobody is a patient who has vanished from the board.</summary>
    [SkippableFact]
    public async Task Booking_then_checking_in_issues_a_queue_ticket_that_belongs_to_the_tenant()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            using var reception = app.ReceptionClient();
            var slots = Slots();
            var made = await reception.PostAsJsonAsync("/api/v1/appointment-slots", slots, Web);
            made.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await made.Content.ReadAsStringAsync());
            var slotId = (await made.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("slots")[0].GetProperty("slotId").GetGuid();

            var booked = await PostAsync(reception, "/api/v1/appointments", Guid.NewGuid().ToString(),
                Booking(slots) with { SlotId = slotId });
            booked.StatusCode.Should().Be(HttpStatusCode.Created, "{0}", await booked.Content.ReadAsStringAsync());
            var appointmentId = (await booked.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("appointmentId").GetGuid();

            var checkedIn = await reception.PostAsJsonAsync(
                $"/api/v1/appointments/{appointmentId}/check-in",
                new { memberNo = "MRS-M-1001", displayName = "Amal Hassan", priority = 0 }, Web);
            checkedIn.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await checkedIn.Content.ReadAsStringAsync());

            await using var db = EmrApiFactory.Ctx();
            var appointment = await db.Appointments.AsNoTracking().SingleAsync(a => a.AppointmentId == appointmentId);
            appointment.Status.Should().Be(AppointmentStatus.CheckedIn);

            var ticket = await db.Set<QueueTicket>().AsNoTracking()
                .SingleAsync(t => t.AppointmentId == appointmentId);
            ticket.TenantId.Should().Be(app.Tenant,
                "a ticket with no tenant is invisible to every real tenant — the patient simply disappears " +
                "from the board, which is how one was found on the dev database");
            ticket.State.Should().Be(QueueTicketState.Waiting);

            app.Outbox.AllMessages.Select(m => m.EventType).Should().Contain("ApptCheckedIn");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_same_slot_cannot_be_booked_twice()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            using var reception = app.ReceptionClient();
            var slots = Slots();
            var made = await reception.PostAsJsonAsync("/api/v1/appointment-slots", slots, Web);
            made.StatusCode.Should().Be(HttpStatusCode.OK);
            var slotId = (await made.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("slots")[0].GetProperty("slotId").GetGuid();

            (await PostAsync(reception, "/api/v1/appointments", Guid.NewGuid().ToString(),
                Booking(slots) with { SlotId = slotId }))
                .StatusCode.Should().Be(HttpStatusCode.Created);

            // A DIFFERENT key, so this is a genuine second booking of the slot and not an idempotent replay.
            var second = await PostAsync(reception, "/api/v1/appointments", Guid.NewGuid().ToString(),
                Booking(slots) with { SlotId = slotId, BeneficiaryId = Guid.NewGuid() });
            second.StatusCode.Should().BeOneOf(HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity);

            await using var db = EmrApiFactory.Ctx();
            (await db.Appointments.CountAsync(a => a.SlotId == slotId && a.Status != AppointmentStatus.Cancelled))
                .Should().Be(1, "two people in one slot is a double-booked clinic");
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- the visit gate -----------------------------------------------------------------------------------

    /// <summary>
    /// A visit is not opened for a member whose benefit is not live. Blocked means NOTHING is persisted — no
    /// encounter shell, no queue entry — because an encounter that exists is one a clinician can write into.
    /// </summary>
    [SkippableTheory]
    [InlineData(MemberStatus.Suspended)]
    [InlineData(MemberStatus.Expired)]
    [InlineData(MemberStatus.Blocked)]
    public async Task A_member_who_is_not_active_gets_no_encounter_and_no_queue_entry(MemberStatus status)
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory { MemberStatus = status };
        try
        {
            using var doctor = app.DoctorClient();
            var beneficiaryId = Guid.NewGuid();
            var r = await StartVisitAsync(doctor, beneficiaryId, Guid.NewGuid().ToString());
            r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            var body = await r.Content.ReadAsStringAsync();
            body.Should().Contain("visit-blocked");
            body.Should().Contain(status.ToString(), "the refusal names the status, or reception cannot act on it");

            await using var db = EmrApiFactory.Ctx();
            (await db.Encounters.CountAsync(e => e.BeneficiaryId == beneficiaryId)).Should().Be(0);
            (await db.QueueEntries.CountAsync(q => q.BeneficiaryId == beneficiaryId)).Should().Be(0);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>An unknown member is blocked too — "policy-service has never heard of them" and "their
    /// benefit is live" are not the same answer, and only one of them opens a visit.</summary>
    [SkippableFact]
    public async Task An_unknown_member_is_blocked_rather_than_given_the_benefit_of_the_doubt()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory { MemberStatus = null };
        try
        {
            using var doctor = app.DoctorClient();
            var r = await StartVisitAsync(doctor, Guid.NewGuid(), Guid.NewGuid().ToString());
            r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await r.Content.ReadAsStringAsync()).Should().Contain("visit-blocked");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>An Active member gets an encounter, a queue entry and the event that puts them on the board —
    /// and a repeat of the same Idempotency-Key returns the same encounter, not a second visit.</summary>
    [SkippableFact]
    public async Task An_active_member_starts_one_visit_however_many_times_the_request_is_retried()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            using var doctor = app.DoctorClient();
            var beneficiaryId = Guid.NewGuid();
            var key = Guid.NewGuid().ToString();

            var first = await StartVisitAsync(doctor, beneficiaryId, key);
            first.StatusCode.Should().Be(HttpStatusCode.Created, "{0}", await first.Content.ReadAsStringAsync());
            var encounterId = (await first.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("encounterId").GetGuid();

            var retry = await StartVisitAsync(doctor, beneficiaryId, key);
            retry.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
            (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("encounterId").GetGuid()
                .Should().Be(encounterId);

            await using var db = EmrApiFactory.Ctx();
            (await db.Encounters.CountAsync(e => e.BeneficiaryId == beneficiaryId)).Should().Be(1);
            (await db.QueueEntries.CountAsync(q => q.BeneficiaryId == beneficiaryId)).Should().Be(1);
            app.Outbox.AllMessages.Select(m => m.EventType).Should().Contain("EncounterStarted");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Starting_a_visit_without_an_idempotency_key_is_refused_and_anonymous_reaches_nothing()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            using var doctor = app.DoctorClient();
            var noKey = await StartVisitAsync(doctor, Guid.NewGuid(), idempotencyKey: null);
            noKey.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            using var anonymous = app.CreateClient();
            (await anonymous.GetAsync(new Uri("/api/v1/appointments", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static readonly Guid Provider = Guid.NewGuid();

    private static CreateSlotsRequest Slots(Guid? branchId = null)
    {
        // A fixed weekday two weeks out, so the generated window never lands in the past and never depends on
        // which day the suite happens to run.
        var from = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(14));
        while (from.DayOfWeek != DayOfWeek.Tuesday) from = from.AddDays(1);
        return new CreateSlotsRequest(
            ProviderId: Guid.NewGuid(), LocationId: Guid.NewGuid(), DoctorId: Guid.NewGuid(),
            DayOfWeek: DayOfWeek.Tuesday, StartTime: new TimeOnly(9, 0), EndTime: new TimeOnly(11, 0),
            SlotMinutes: 30, FromDate: from, ToDate: from, BranchId: branchId);
    }

    private static BookAppointmentRequest Booking(CreateSlotsRequest slots) => new(
        BeneficiaryId: Guid.NewGuid(), ProviderId: slots.ProviderId, LocationId: slots.LocationId,
        AppointmentType: nameof(Mersal.Emr.Domain.AppointmentType.Scheduled), SlotId: null, ScheduledStart: null, ScheduledEnd: null,
        ReferralRef: null, OriginEncounterId: null, JoinWaitlistIfFull: false);

    private static Task<HttpResponseMessage> StartVisitAsync(
        HttpClient client, Guid beneficiaryId, string? idempotencyKey) =>
        PostAsync(client, "/api/v1/encounters", idempotencyKey,
            new { beneficiaryId, providerId = Provider, appointmentId = (Guid?)null });

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, string? idempotencyKey, object body)
    {
        // Awaited inside the using: returning the task would dispose the content mid-send.
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Relative))
        {
            Content = JsonContent.Create(body, body.GetType(), options: Web),
        };
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(req);
    }
}

/// <summary>Serializes the emr endpoint tests — they share the emr store with the other DB suites.</summary>
[Xunit.CollectionDefinition("emr-db", DisableParallelization = true)]
public sealed class EmrDbTestGroup;
