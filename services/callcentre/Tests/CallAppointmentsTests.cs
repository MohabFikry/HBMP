using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Mersal.CallCentre.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.CallCentre.Tests;

/// <summary>Real-endpoint tests for 15.3 (env-gated): booking/rescheduling/cancelling delegate to the emr gateway
/// only for a VERIFIED caller (else 403), cancel demands a reason (else 422), Idempotency-Key + If-Match are
/// forwarded verbatim, and each success links the change to the interaction and queues a notification.</summary>
[Collection("callcentre-db")]
public class CallAppointmentsTests(CallCentreFactory factory) : IClassFixture<CallCentreFactory>
{
    private async Task<Guid> OpenAndVerifyAsync(HttpClient client, Guid ben)
    {
        var open = await client.PostAsJsonAsync("/api/v1/call-interactions", new { direction = "Inbound", reasonCode = "BookAppointment" });
        var id = (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("interactionId").GetGuid();
        await client.PostAsJsonAsync($"/api/v1/call-interactions/{id}/verification",
            new { beneficiaryId = ben, verifiedIdentifierTypes = new[] { "MemberNo", "Phone" }, result = "Passed" });
        return id;
    }

    [SkippableFact]
    public async Task Verified_booking_delegates_links_and_notifies()
    {
        Skip.If(CallCentreFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var client = factory.AgentClient();
        var ben = factory.Directory.BeneficiaryId;
        try
        {
            var interactionId = await OpenAndVerifyAsync(client, ben);
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/call-centre/appointments")
            {
                Content = JsonContent.Create(new { interactionId, beneficiaryId = ben, slotId = Guid.NewGuid(), appointmentType = "Consultation", branchId = Guid.NewGuid() }),
            };
            req.Headers.TryAddWithoutValidation("Idempotency-Key", "idem-book-1");
            var resp = await client.SendAsync(req);
            resp.StatusCode.Should().Be(HttpStatusCode.Created);

            factory.Gateway.LastIdempotencyKey.Should().Be("idem-book-1");   // forwarded verbatim
            factory.Outbox.AllMessages.Should().Contain(m => m.EventType == "AppointmentConfirmationRequested");

            await using var db = Db();
            (await db.AppointmentLinks.AnyAsync(l => l.InteractionId == interactionId && l.AppointmentId == factory.Gateway.BookedAppointmentId))
                .Should().BeTrue();
        }
        finally { await CleanAsync(); }
    }

    [SkippableFact]
    public async Task Unverified_booking_is_403()
    {
        Skip.If(CallCentreFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var client = factory.AgentClient();
        var ben = factory.Directory.BeneficiaryId;
        try
        {
            var open = await client.PostAsJsonAsync("/api/v1/call-interactions", new { direction = "Inbound" });
            var interactionId = (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("interactionId").GetGuid();
            var resp = await client.PostAsJsonAsync("/api/v1/call-centre/appointments",
                new { interactionId, beneficiaryId = ben, slotId = Guid.NewGuid(), appointmentType = "Consultation" });
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await CleanAsync(); }
    }

    [SkippableFact]
    public async Task Cancel_without_reason_is_422()
    {
        Skip.If(CallCentreFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var client = factory.AgentClient();
        try
        {
            var interactionId = await OpenAndVerifyAsync(client, factory.Directory.BeneficiaryId);
            var resp = await client.PostAsJsonAsync($"/api/v1/call-centre/appointments/{Guid.NewGuid()}/cancel",
                new { interactionId, note = "no reason given" });
            resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        }
        finally { await CleanAsync(); }
    }

    [SkippableFact]
    public async Task Reschedule_forwards_if_match()
    {
        Skip.If(CallCentreFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var client = factory.AgentClient();
        try
        {
            var interactionId = await OpenAndVerifyAsync(client, factory.Directory.BeneficiaryId);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/call-centre/appointments/{Guid.NewGuid()}/reschedule")
            {
                Content = JsonContent.Create(new { interactionId, newSlotId = Guid.NewGuid() }),
            };
            req.Headers.TryAddWithoutValidation("If-Match", "\"42\"");
            var resp = await client.SendAsync(req);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            factory.Gateway.LastIfMatch.Should().Be("\"42\"");
        }
        finally { await CleanAsync(); }
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
