using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.CallCentre.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.CallCentre.Tests;

/// <summary>Real-endpoint tests for 15.2 through the web host (env-gated <c>CALLCENTRE_TEST_DB</c>): pre-verification
/// search is thin (no coverage/appointments), the 360 is 403 until a verification PASS is recorded on the
/// interaction, then 200 with appointments across MULTIPLE branches and NO clinical token in the serialized JSON.
/// Serialized via the callcentre-db collection; self-cleans by tenant.</summary>
[Collection("callcentre-db")]
public class MembersEndpointTests(CallCentreFactory factory) : IClassFixture<CallCentreFactory>
{
    private static readonly string[] Clinical =
        ["diagnos", "icd", "prescription", "medication", "vital", "examination", "soap", "allerg"];

    [Fact]
    public async Task Search_before_verification_is_thin_then_360_gates_on_verification()
    {
        if (CallCentreFactory.Db is null) return;
        var client = factory.AgentClient();
        var ben = factory.Directory.BeneficiaryId;
        try
        {
            // Open a call.
            var open = await client.PostAsJsonAsync("/api/v1/call-interactions", new { direction = "Inbound", reasonCode = "AppointmentEnquiry" });
            open.StatusCode.Should().Be(HttpStatusCode.Created);
            var interactionId = (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("interactionId").GetGuid();

            // Pre-verification search: only name + id + identifier types — never coverage/appointments/contacts.
            var search = await client.GetAsync("/api/v1/call-centre/search?q=%2B20100000000");
            search.StatusCode.Should().Be(HttpStatusCode.OK);
            var searchJson = (await search.Content.ReadAsStringAsync()).ToLowerInvariant();
            searchJson.Should().Contain("challengeableidentifiertypes");
            searchJson.Should().NotContain("coverage").And.NotContain("remaininglimit").And.NotContain("appointment");

            // 360 BEFORE verification → 403.
            var before = await client.GetAsync($"/api/v1/call-centre/members/{ben}/summary?interactionId={interactionId}");
            before.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            // Record a PASS with two identifier types → binds the interaction.
            var verify = await client.PostAsJsonAsync($"/api/v1/call-interactions/{interactionId}/verification",
                new { beneficiaryId = ben, verifiedIdentifierTypes = new[] { "MemberNo", "DateOfBirth" }, result = "Passed" });
            verify.StatusCode.Should().Be(HttpStatusCode.OK);

            // 360 AFTER verification → 200, cross-branch appointments, no clinical token.
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

    [Fact]
    public async Task One_identifier_type_pass_is_rejected_422()
    {
        if (CallCentreFactory.Db is null) return;
        var client = factory.AgentClient();
        try
        {
            var open = await client.PostAsJsonAsync("/api/v1/call-interactions", new { direction = "Inbound" });
            var interactionId = (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("interactionId").GetGuid();
            var verify = await client.PostAsJsonAsync($"/api/v1/call-interactions/{interactionId}/verification",
                new { beneficiaryId = factory.Directory.BeneficiaryId, verifiedIdentifierTypes = new[] { "MemberNo" }, result = "Passed" });
            verify.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
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
