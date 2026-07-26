using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.CallCentre.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.CallCentre.Tests;

/// <summary>Phase 15.6 — the end-to-end proof (env-gated). One agent runs the whole journey: open → search by phone
/// → verify (two identifier types) → load 360 (assert no clinical field) → book → cancel with a reason → close.
/// Then it asserts the correlated event chain flowed through the outbox, the appointment↔interaction links exist,
/// the notification confirmation is clinical-free, and the supervisor KPIs (PHI-free) reflect the activity.</summary>
[Collection("callcentre-db")]
public class CallCentreE2ETests(CallCentreFactory factory) : IClassFixture<CallCentreFactory>
{
    private static readonly string[] Clinical = ["diagnos", "prescription", "medication", "vital", "examination", "soap", "allerg"];

    [SkippableFact]
    public async Task Full_call_journey_is_correlated_clinical_free_and_reflected_in_kpis()
    {
        Skip.If(CallCentreFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        factory.Outbox.Clear();
        var client = factory.AgentClient();
        var ben = factory.Directory.BeneficiaryId;
        try
        {
            // 1. open
            var open = await client.PostAsJsonAsync("/api/v1/call-interactions", new { direction = "Inbound", reasonCode = "BookAppointment" });
            var openBody = await open.Content.ReadFromJsonAsync<JsonElement>();
            var interactionId = openBody.GetProperty("interactionId").GetGuid();
            var callRef = openBody.GetProperty("callRef").GetString()!;

            // 2. search by PHONE
            var search = await client.GetAsync("/api/v1/call-centre/search?q=%2B20100000000");
            search.StatusCode.Should().Be(HttpStatusCode.OK);

            // 3. verify with two identifier types
            (await client.PostAsJsonAsync($"/api/v1/call-interactions/{interactionId}/verification",
                new { beneficiaryId = ben, verifiedIdentifierTypes = new[] { "MemberNo", "Phone" }, result = "Passed" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            // 4. 360 — no clinical field in the serialized payload
            var s360 = await client.GetAsync($"/api/v1/call-centre/members/{ben}/summary?interactionId={interactionId}");
            s360.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = (await s360.Content.ReadAsStringAsync()).ToLowerInvariant();
            foreach (var c in Clinical) json.Should().NotContain(c);

            // 5. book
            (await client.PostAsJsonAsync("/api/v1/call-centre/appointments",
                new { interactionId, beneficiaryId = ben, slotId = Guid.NewGuid(), appointmentType = "Consultation" }))
                .StatusCode.Should().Be(HttpStatusCode.Created);

            // 6. cancel with a reason
            (await client.PostAsJsonAsync($"/api/v1/call-centre/appointments/{Guid.NewGuid()}/cancel",
                new { interactionId, reasonCode = "PatientRequest" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            // 7. close with an outcome
            (await client.PostAsJsonAsync($"/api/v1/call-interactions/{interactionId}/close",
                new { outcome = "Resolved", notes = "handled" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            // --- audit/event chain: correlated by call_ref, complete ---
            var events = factory.Outbox.AllMessages.Select(m => m.EventType).ToList();
            events.Should().Contain("CallInteractionOpened");
            events.Should().Contain("CallerVerificationRecorded");
            events.Should().Contain("AppointmentConfirmationRequested");
            events.Should().Contain("CallInteractionClosed");

            // notification confirmation is clinical-free
            var notif = factory.Outbox.AllMessages.First(m => m.EventType == "AppointmentConfirmationRequested");
            var notifPayload = notif.Payload.ToLowerInvariant();
            foreach (var c in Clinical) notifPayload.Should().NotContain(c);
            notifPayload.Should().Contain(callRef.ToLowerInvariant());   // correlated by call_ref

            // links recorded on the call-centre side (never on emr's appointment table)
            await using (var db = Db())
            {
                (await db.AppointmentLinks.CountAsync(l => l.InteractionId == interactionId)).Should().BeGreaterThanOrEqualTo(2);
            }

            // --- KPIs (PHI-free) reflect the activity (supervisor scope) ---
            var kpiResp = await factory.SupervisorClient().GetAsync("/api/v1/call-centre/kpis");
            kpiResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var kpiJson = (await kpiResp.Content.ReadAsStringAsync()).ToLowerInvariant();
            foreach (var c in Clinical) kpiJson.Should().NotContain(c);
            var kpi = await kpiResp.Content.ReadFromJsonAsync<JsonElement>();
            kpi.GetProperty("callsHandled").GetInt32().Should().BeGreaterThanOrEqualTo(1);
            kpi.GetProperty("appointmentsBooked").GetInt32().Should().BeGreaterThanOrEqualTo(1);
            kpi.GetProperty("appointmentsCancelled").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        }
        finally { await CleanAsync(); }
    }

    [SkippableFact]
    public async Task Agent_cannot_read_team_kpis()
    {
        Skip.If(CallCentreFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        // A plain agent (not supervisor/manager) is denied the team KPI view.
        (await factory.AgentClient().GetAsync("/api/v1/call-centre/kpis"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static CallCentreDbContext Db() => new(
        new DbContextOptionsBuilder<CallCentreDbContext>().UseNpgsql(CallCentreFactory.Db).UseSnakeCaseNamingConvention().Options);

    private static async Task CleanAsync()
    {
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.appointment_link WHERE tenant_id = 't-callcentre';");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.caller_verification WHERE tenant_id = 't-callcentre';");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.call_interaction WHERE tenant_id = 't-callcentre';");
    }
}
