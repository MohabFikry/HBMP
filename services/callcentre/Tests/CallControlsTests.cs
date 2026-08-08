using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.CallCentre.Domain;
using Mersal.CallCentre.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.CallCentre.Tests;

/// <summary>
/// The controls that govern a call record and what it may disclose (env-gated <c>CALLCENTRE_TEST_DB</c>).
///
/// <list type="number">
///   <item><b>Ownership on the write paths.</b> <c>CallCentrePolicies.Interaction</c> is described as "the
///   agent's own calls", and the LIST endpoint narrowed a non-supervisor to exactly that — but patch, close
///   and summary-edit checked role + tenant only, so any agent could rewrite a colleague's call record.</item>
///   <item><b>The interaction binding.</b> Identity is now confirmed on the phone, so the gate no longer judges
///   a challenge. What it still enforces — and what these tests pin — is that a call may only disclose the
///   member it was opened against.</item>
///   <item><b>Closing is the expiry.</b> It is now the ONLY one, so it has to work, and the absence of a
///   time-based expiry has to be deliberate rather than an oversight someone "fixes" later.</item>
/// </list>
/// </summary>
[Collection("callcentre-db")]
public class CallControlsTests(CallCentreFactory factory) : IClassFixture<CallCentreFactory>
{
    private const string Skipped = "test DB not configured — set the *_TEST_DB env var to run this DB integration test.";

    private static async Task<Guid> OpenCallAsync(HttpClient client)
    {
        var open = await client.PostAsJsonAsync("/api/v1/call-interactions",
            new { direction = "Inbound", reasonCode = "AppointmentEnquiry" });
        open.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("interactionId").GetGuid();
    }

    /// <summary>Open a member's file on this call — the single request the agent's client makes when they pick a
    /// search hit, replacing the identifier-challenge step.</summary>
    private static Task<HttpResponseMessage> OpenMemberAsync(HttpClient client, Guid interactionId, Guid beneficiaryId) =>
        client.PostAsJsonAsync($"/api/v1/call-interactions/{interactionId}/verification",
            new { beneficiaryId });

    [SkippableFact]
    public async Task Another_agent_cannot_patch_close_or_rewrite_someone_elses_call()
    {
        Skip.If(CallCentreFactory.Db is null, Skipped);
        var mine = factory.AgentClient();
        var theirs = factory.OtherAgentClient();
        try
        {
            var interactionId = await OpenCallAsync(mine);

            // Same role, same tenant, same scopes — the policy engine cannot tell these two apart, which is
            // exactly why the check has to live in the endpoint.
            (await theirs.PatchAsJsonAsync($"/api/v1/call-interactions/{interactionId}",
                new { summary = "not my call" }))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);

            (await theirs.PostAsJsonAsync($"/api/v1/call-interactions/{interactionId}/close",
                new { outcome = "Resolved", summary = "Closing a call I did not take." }))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);

            (await theirs.PatchAsJsonAsync($"/api/v1/call-interactions/{interactionId}/summary",
                new { summary = "Rewriting the record another role reads." }))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);

            // The owning agent is unaffected — the rule is "not yours", not "nobody's".
            (await mine.PostAsJsonAsync($"/api/v1/call-interactions/{interactionId}/close",
                new { outcome = "Resolved", summary = "Answered an appointment enquiry; no change made." }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally { await CleanAsync(); }
    }

    [SkippableFact]
    public async Task A_supervisor_may_still_correct_the_team_s_records()
    {
        Skip.If(CallCentreFactory.Db is null, Skipped);
        var agent = factory.AgentClient();
        var supervisor = factory.SupervisorClient();
        try
        {
            var interactionId = await OpenCallAsync(agent);
            // The ownership rule must not cost supervisors the QA correction the team view exists for.
            (await supervisor.PostAsJsonAsync($"/api/v1/call-interactions/{interactionId}/close",
                new { outcome = "Resolved", summary = "Closed on the agent's behalf after the shift ended." }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally { await CleanAsync(); }
    }

    /// <summary>Opening a member's file records an OFF-SYSTEM attestation, binds the call, and discloses — with
    /// no identifier types submitted and none stored.</summary>
    [SkippableFact]
    public async Task Opening_a_member_file_attests_off_system_and_discloses()
    {
        Skip.If(CallCentreFactory.Db is null, Skipped);
        var client = factory.AgentClient();
        var ben = factory.Directory.BeneficiaryId;
        try
        {
            var interactionId = await OpenCallAsync(client);

            // No identifier types, no result, no threshold — the caller was confirmed on the phone.
            var attested = await OpenMemberAsync(client, interactionId, ben);
            attested.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await attested.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("result").GetString().Should().Be("Passed");
            body.GetProperty("method").GetString().Should().Be("OffSystem");
            body.GetProperty("verifiedIdentifierTypes").GetArrayLength().Should()
                .Be(0, "the agent does not report which identifiers they asked for, so storing a set would invent evidence");

            (await client.GetAsync($"/api/v1/call-centre/members/{ben}/summary?interactionId={interactionId}"))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally { await CleanAsync(); }
    }

    /// <summary>THE RULE THAT SURVIVED. A call discloses the member it was opened against and no other — so an
    /// agent cannot open a file on one member and then read a second member's details through the same call.</summary>
    [SkippableFact]
    public async Task A_call_cannot_disclose_a_member_it_was_not_opened_against()
    {
        Skip.If(CallCentreFactory.Db is null, Skipped);
        var client = factory.AgentClient();
        var ben = factory.Directory.BeneficiaryId;
        var someoneElse = Guid.NewGuid();
        try
        {
            var interactionId = await OpenCallAsync(client);
            (await OpenMemberAsync(client, interactionId, ben)).StatusCode.Should().Be(HttpStatusCode.OK);

            (await client.GetAsync($"/api/v1/call-centre/members/{someoneElse}/summary?interactionId={interactionId}"))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, "the binding is to ONE beneficiary, not to 'a member was opened'");
        }
        finally { await CleanAsync(); }
    }

    /// <summary>Closing the call ends disclosure. It is now the only expiry, so it carries the whole weight.</summary>
    [SkippableFact]
    public async Task Closing_the_call_ends_disclosure()
    {
        Skip.If(CallCentreFactory.Db is null, Skipped);
        var client = factory.AgentClient();
        var ben = factory.Directory.BeneficiaryId;
        try
        {
            var interactionId = await OpenCallAsync(client);
            (await OpenMemberAsync(client, interactionId, ben)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.GetAsync($"/api/v1/call-centre/members/{ben}/summary?interactionId={interactionId}"))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            (await client.PostAsJsonAsync($"/api/v1/call-interactions/{interactionId}/close",
                new { outcome = "Resolved", summary = "Confirmed the appointment time and ended the call." }))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            (await client.GetAsync($"/api/v1/call-centre/members/{ben}/summary?interactionId={interactionId}"))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await CleanAsync(); }
    }

    /// <summary>
    /// An attestation does NOT age out while the call is open — and that is deliberate.
    ///
    /// <para>A 60-minute TTL used to sit here, and it was right for the control it guarded: an identifier
    /// recited at the start of a call is weak evidence about who is on the line an hour later. What the record
    /// asserts now is that the AGENT IS SPEAKING TO THIS PERSON, which does not decay across the call — it ends
    /// with the call. Re-adding a TTL would hand a long call a silent 403 the agent can neither see coming nor
    /// fix, on a clock that measures nothing.</para>
    ///
    /// <para>This test exists so that removal reads as a decision. Without it, the next person to notice there is
    /// no time limit has only the absence of a test to go on.</para>
    /// </summary>
    [SkippableFact]
    public async Task An_attestation_does_not_age_out_while_the_call_is_open()
    {
        Skip.If(CallCentreFactory.Db is null, Skipped);
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..8];
        var beneficiary = Guid.NewGuid();
        Guid interactionId;
        var options = new DbContextOptionsBuilder<CallCentreDbContext>()
            .UseNpgsql(CallCentreFactory.Db).UseSnakeCaseNamingConvention().Options;
        try
        {
            await using (var db = new CallCentreDbContext(options))
            {
                var i = new CallInteraction
                {
                    InteractionId = Guid.NewGuid(), CallRef = await new CallRefIssuer(db).NextAsync(2026),
                    TenantId = tenant, AgentUserId = Guid.NewGuid(), Direction = CallDirection.Inbound,
                    StartedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow, BeneficiaryId = beneficiary,
                };
                db.Interactions.Add(i);
                db.Verifications.Add(new CallerVerification
                {
                    VerificationId = Guid.NewGuid(), InteractionId = i.InteractionId, BeneficiaryId = beneficiary,
                    TenantId = tenant, VerifiedIdentifierTypes = [],
                    Result = VerificationResult.Passed, Method = VerificationMethod.OffSystem,
                    // Well past the hour the old TTL allowed.
                    VerifiedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(4),
                });
                await db.SaveChangesAsync();
                interactionId = i.InteractionId;
            }

            await using (var db = new CallCentreDbContext(options))
            {
                var i = await db.Interactions.AsNoTracking().FirstAsync(x => x.InteractionId == interactionId);
                i.Status.Should().Be(InteractionStatus.Open);

                (await new VerificationService(db).IsVerifiedAsync(interactionId, beneficiary))
                    .Should().BeTrue("a four-hour call is a long call, not an unverified one");
            }

            // Closing it — the one expiry — still ends disclosure.
            await using (var db = new CallCentreDbContext(options))
            {
                var i = await db.Interactions.FirstAsync(x => x.InteractionId == interactionId);
                i.Status = InteractionStatus.Closed;
                await db.SaveChangesAsync();
            }

            await using (var db = new CallCentreDbContext(options))
            {
                (await new VerificationService(db).IsVerifiedAsync(interactionId, beneficiary))
                    .Should().BeFalse("closing the call is now the only expiry, so it has to be the one that works");
            }
        }
        finally
        {
            await using var db = new CallCentreDbContext(options);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.caller_verification WHERE tenant_id = {0};", tenant);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.call_interaction WHERE tenant_id = {0};", tenant);
        }
    }

    /// <summary>Historical on-system rows keep their method. The DDL default is what makes this true, and it is
    /// the reason old audit evidence is not silently re-labelled — so it is asserted, not assumed.</summary>
    [SkippableFact]
    public async Task A_row_written_without_a_method_is_recorded_as_on_system()
    {
        Skip.If(CallCentreFactory.Db is null, Skipped);
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..8];
        var options = new DbContextOptionsBuilder<CallCentreDbContext>()
            .UseNpgsql(CallCentreFactory.Db).UseSnakeCaseNamingConvention().Options;
        try
        {
            await using var db = new CallCentreDbContext(options);
            var interactionId = Guid.NewGuid();
            var beneficiary = Guid.NewGuid();
            var verificationId = Guid.NewGuid();

            db.Interactions.Add(new CallInteraction
            {
                InteractionId = interactionId, CallRef = await new CallRefIssuer(db).NextAsync(2026),
                TenantId = tenant, AgentUserId = Guid.NewGuid(), Direction = CallDirection.Inbound,
                StartedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow, BeneficiaryId = beneficiary,
            });
            await db.SaveChangesAsync();

            // INSERT with no `method` — exactly what every row written before 0006 looks like.
            await db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO callcentre.caller_verification
                      (verification_id, interaction_id, beneficiary_id, tenant_id, verified_identifiers, result, verified_at)
                  VALUES ({0}, {1}, {2}, {3}, '[""MemberNo"",""DateOfBirth""]'::jsonb, 'Passed', now());",
                verificationId, interactionId, beneficiary, tenant);

            var row = await db.Verifications.AsNoTracking().FirstAsync(v => v.VerificationId == verificationId);
            row.Method.Should().Be(VerificationMethod.OnSystem,
                "it WAS an on-screen challenge; back-dating it into an off-system attestation would misreport what the platform did");
        }
        finally
        {
            await using var db = new CallCentreDbContext(options);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.caller_verification WHERE tenant_id = {0};", tenant);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.call_interaction WHERE tenant_id = {0};", tenant);
        }
    }

    private static async Task CleanAsync()
    {
        await using var db = new CallCentreDbContext(
            new DbContextOptionsBuilder<CallCentreDbContext>().UseNpgsql(CallCentreFactory.Db).UseSnakeCaseNamingConvention().Options);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.caller_verification WHERE tenant_id = 't-callcentre';");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.call_interaction WHERE tenant_id = 't-callcentre';");
    }
}
