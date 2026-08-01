using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.CallCentre.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.CallCentre.Tests;

/// <summary>Real-endpoint tests for 15.2 through the web host (env-gated <c>CALLCENTRE_TEST_DB</c>): a search hit
/// is thin (name + member number, no coverage/appointments), the 360 is 403 until the call is bound to that
/// beneficiary, then 200 with appointments across MULTIPLE branches and NO clinical token in the serialized JSON.
/// Serialized via the callcentre-db collection; self-cleans by tenant.</summary>
[Collection("callcentre-db")]
public class MembersEndpointTests(CallCentreFactory factory) : IClassFixture<CallCentreFactory>
{
    private static readonly string[] Clinical =
        ["diagnos", "icd", "prescription", "medication", "vital", "examination", "soap", "allerg"];

    [SkippableFact]
    public async Task Search_is_thin_then_the_360_gates_on_the_call_being_bound()
    {
        Skip.If(CallCentreFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var client = factory.AgentClient();
        var ben = factory.Directory.BeneficiaryId;
        try
        {
            // Open a call.
            var open = await client.PostAsJsonAsync("/api/v1/call-interactions", new { direction = "Inbound", reasonCode = "AppointmentEnquiry" });
            open.StatusCode.Should().Be(HttpStatusCode.Created);
            var interactionId = (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("interactionId").GetGuid();

            // A search hit is a way to pick the right person, not a disclosure: name + member number and nothing
            // else. The member number is now unmasked — there is no identifier challenge left for it to weaken.
            var search = await client.GetAsync("/api/v1/call-centre/search?q=%2B20100000000");
            search.StatusCode.Should().Be(HttpStatusCode.OK);
            var searchJson = (await search.Content.ReadAsStringAsync()).ToLowerInvariant();
            searchJson.Should().Contain("mrs-m-1001");
            searchJson.Should().NotContain("challengeable").And.NotContain("masked");
            searchJson.Should().NotContain("coverage").And.NotContain("remaininglimit").And.NotContain("appointment");

            // 360 before the call is bound to this member → 403.
            var before = await client.GetAsync($"/api/v1/call-centre/members/{ben}/summary?interactionId={interactionId}");
            before.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            // Opening the member's file attests + binds. No identifier types are submitted.
            var attest = await client.PostAsJsonAsync($"/api/v1/call-interactions/{interactionId}/verification",
                new { beneficiaryId = ben });
            attest.StatusCode.Should().Be(HttpStatusCode.OK);

            // 360 after → 200, cross-branch appointments, no clinical token.
            var after = await client.GetAsync($"/api/v1/call-centre/members/{ben}/summary?interactionId={interactionId}");
            after.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = (await after.Content.ReadAsStringAsync()).ToLowerInvariant();
            json.Should().Contain("aswan").And.Contain("maadi");   // appointments from every branch
            foreach (var c in Clinical) json.Should().NotContain(c);
        }
        finally
        {
            await CleanAsync();
        }
    }

    /// <summary>Identifier types sent by a client are IGNORED, not honoured and not rejected.
    ///
    /// <para>The endpoint no longer judges a challenge, so a client that still posts a type list must not be able
    /// to write one into the audit record: a stored set would read as evidence the agent confirmed those
    /// identifiers, which nobody checked and nobody asserted.</para></summary>
    [SkippableFact]
    public async Task Identifier_types_sent_by_a_client_are_not_recorded()
    {
        Skip.If(CallCentreFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var client = factory.AgentClient();
        try
        {
            var open = await client.PostAsJsonAsync("/api/v1/call-interactions", new { direction = "Inbound" });
            var interactionId = (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("interactionId").GetGuid();

            var attest = await client.PostAsJsonAsync($"/api/v1/call-interactions/{interactionId}/verification",
                new { beneficiaryId = factory.Directory.BeneficiaryId, verifiedIdentifierTypes = new[] { "MemberNo", "DateOfBirth" }, result = "Failed" });

            attest.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await attest.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("verifiedIdentifierTypes").GetArrayLength().Should().Be(0);
            body.GetProperty("result").GetString().Should().Be("Passed", "there is nothing left to fail");
            body.GetProperty("method").GetString().Should().Be("OffSystem");
        }
        finally { await CleanAsync(); }
    }

    private static async Task CleanAsync()
    {
        await using var db = new CallCentreDbContext(
            new DbContextOptionsBuilder<CallCentreDbContext>().UseNpgsql(CallCentreFactory.Db).UseSnakeCaseNamingConvention().Options);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.caller_verification WHERE tenant_id = 't-callcentre';");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.call_interaction WHERE tenant_id = 't-callcentre';");
    }
}
