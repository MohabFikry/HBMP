using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Emr.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Tests;

/// <summary>
/// 32.6 — the waiting room over HTTP (design 14 §3.3).
///
/// <para>Five endpoints served this queue and nothing in the product called any of them for four phases,
/// while the WRITE half ran on every check-in: tickets were issued, never read, never ordered and never
/// cleared. The reason was in the signature. <c>GET /queues</c> required a <c>locationId</c> AND a
/// <c>providerId</c> as mandatory Guids, and a reception desk has neither — it knows its branch. The only
/// question the endpoint could answer was one nobody at a desk was in a position to ask.</para>
///
/// <para>These tests pin the two halves of the fix: the desk can now ask, and a caller who is NOT narrowed to
/// a branch still cannot ask it unfiltered — because dropping a required parameter must not widen a
/// disclosure as a side effect.</para>
/// </summary>
[Collection("emr-db")]
public class WaitingRoomEndpointTests
{
    private static readonly Guid Dokki = Guid.Parse("d0kk1000-0000-4000-8000-00000000000d".Replace("k", "1"));

    [SkippableFact]
    public async Task A_branch_desk_can_ask_who_is_waiting_without_naming_a_clinic()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory { HomeBranch = Dokki };
        try
        {
            await SeedWaitingAsync(app, "MRS-M-2026-000009", "Amal Hassan", priority: 0);

            using var reception = app.ReceptionClient();
            var r = await reception.GetAsync(new Uri("/api/v1/queues", UriKind.Relative));

            // THE test. This used to be a 400: locationId and providerId were non-nullable Guids, so a call
            // with neither could not bind, and the desk had nothing to send.
            r.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await r.Content.ReadAsStringAsync());

            var rows = await r.Content.ReadFromJsonAsync<List<JsonElement>>() ?? [];
            rows.Should().NotBeEmpty("a ticket was issued at check-in and this is what reads it");
            rows[0].GetProperty("memberNo").GetString().Should().Be("MRS-M-2026-000009");
            rows[0].GetProperty("position").GetInt32().Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_unnarrowed_caller_is_refused_rather_than_shown_every_branch()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory();
        try
        {
            await SeedWaitingAsync(app, "MRS-M-2026-000031", "Nour Adel", priority: 0);

            // The call centre holds appointment:read and is MemberScoped — ApplyBranchScope is deliberately
            // unrestricted for that mode. An unfiltered call would therefore have listed every person waiting
            // in every branch on the platform, as a side effect of making a parameter optional.
            using var callCentre = app.As("22222222-2222-2222-2222-222222222222", "call_center",
                "appointment:read");
            var r = await callCentre.GetAsync(new Uri("/api/v1/queues", UriKind.Relative));

            r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await r.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("title").GetString().Should().Be("queue-scope-required");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Calling_next_moves_the_head_and_the_board_agrees()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory { HomeBranch = Dokki };
        try
        {
            // Priority leads arrival: the second person to check in is the one who should be called.
            await SeedWaitingAsync(app, "MRS-EARLY", "Early Arrival", priority: 0);
            await SeedWaitingAsync(app, "MRS-URGENT", "Urgent Case", priority: 5);

            using var reception = app.ReceptionClient();
            var called = await reception.PostAsync(new Uri("/api/v1/queues/call-next", UriKind.Relative), null);
            called.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await called.Content.ReadAsStringAsync());

            (await called.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("memberNo").GetString().Should().Be("MRS-URGENT",
                    "the order is the server's — priority then arrival — and the board must not re-sort it");

            // And the person called is off the waiting list, so the desk does not call them twice.
            var rows = await (await reception.GetAsync(new Uri("/api/v1/queues", UriKind.Relative)))
                .Content.ReadFromJsonAsync<List<JsonElement>>() ?? [];
            rows.Should().ContainSingle();
            rows[0].GetProperty("memberNo").GetString().Should().Be("MRS-EARLY");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_empty_waiting_room_is_an_answer_not_an_error()
    {
        Skip.If(EmrApiFactory.Db is null, "EMR_TEST_DB not set — DB integration test skipped.");
        await using var app = new EmrApiFactory { HomeBranch = Dokki };
        try
        {
            using var reception = app.ReceptionClient();
            var r = await reception.PostAsync(new Uri("/api/v1/queues/call-next", UriKind.Relative), null);

            // 204, which the client maps to null and the screen says out loud. A failure here would make a
            // quiet morning look like a broken button.
            r.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>A Waiting ticket at the datastore — the state check-in leaves behind.</summary>
    private static async Task SeedWaitingAsync(EmrApiFactory app, string memberNo, string name, int priority)
    {
        await using var db = EmrApiFactory.Ctx();
        db.Set<QueueTicket>().Add(new QueueTicket
        {
            QueueId = Guid.NewGuid(), AppointmentId = Guid.NewGuid(), BeneficiaryId = Guid.NewGuid(),
            TenantId = app.Tenant, ProviderId = Guid.NewGuid(), LocationId = Guid.NewGuid(),
            // The ticket inherits its appointment's branch in production; here it is set directly, because a
            // ticket with no branch is invisible to a branch-narrowed desk — correctly, and that is exactly
            // the case a fixture must not accidentally test instead of the one it means to.
            BranchId = Dokki,
            MemberNo = memberNo, DisplayName = name, Priority = priority,
            State = QueueTicketState.Waiting, EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-priority - 1),
        });
        await db.SaveChangesAsync();
    }
}
