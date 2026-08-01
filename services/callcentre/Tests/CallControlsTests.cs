using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.CallCentre.Domain;
using Mersal.CallCentre.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.CallCentre.Tests;

/// <summary>
/// The three controls added after the phase-15/20 audit, each proving a rule that the service DOCUMENTED but
/// did not enforce (env-gated <c>CALLCENTRE_TEST_DB</c>).
///
/// <list type="number">
///   <item><b>Ownership on the write paths.</b> <c>CallCentrePolicies.Interaction</c> is described as "the
///   agent's own calls", and the LIST endpoint narrowed a non-supervisor to exactly that — but patch, close
///   and summary-edit checked role + tenant only, so any agent could rewrite a colleague's call record.</item>
///   <item><b>A verification attempt cap.</b> Every failure was persisted and audited; none of them stopped
///   the next attempt.</item>
///   <item><b>Time-based expiry of a verification.</b> Closing the interaction was the only expiry, which made
///   the control depend on a later request succeeding.</item>
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
                new { notes = "not my call" }))
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

    [SkippableFact]
    public async Task Verification_locks_out_after_the_configured_number_of_failures()
    {
        Skip.If(CallCentreFactory.Db is null, Skipped);
        var client = factory.AgentClient();
        var ben = factory.Directory.BeneficiaryId;
        try
        {
            var interactionId = await OpenCallAsync(client);

            for (var attempt = 0; attempt < VerificationPolicy.MaxFailedAttempts; attempt++)
            {
                var fail = await client.PostAsJsonAsync($"/api/v1/call-interactions/{interactionId}/verification",
                    new { beneficiaryId = ben, verifiedIdentifierTypes = new[] { "MemberNo" }, result = "Failed" });
                fail.StatusCode.Should().Be(HttpStatusCode.OK, "a failure is recorded and audited, not refused");
            }

            // The next attempt is refused before it is even evaluated…
            var locked = await client.PostAsJsonAsync($"/api/v1/call-interactions/{interactionId}/verification",
                new { beneficiaryId = ben, verifiedIdentifierTypes = new[] { "MemberNo", "DateOfBirth" }, result = "Failed" });
            locked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

            // …including one that WOULD have passed. Otherwise the cap only slows a guesser down.
            var passAfterLockout = await client.PostAsJsonAsync($"/api/v1/call-interactions/{interactionId}/verification",
                new { beneficiaryId = ben, verifiedIdentifierTypes = new[] { "MemberNo", "DateOfBirth" }, result = "Passed" });
            passAfterLockout.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

            // And nothing was disclosed on the way through.
            (await client.GetAsync($"/api/v1/call-centre/members/{ben}/summary?interactionId={interactionId}"))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await CleanAsync(); }
    }

    /// <summary>
    /// A Passed verification stops unlocking the 360 once it is older than the TTL, even though the interaction
    /// is still Open.
    ///
    /// <para>This is the one that mattered most. Closing was the ONLY expiry, and the client's close request had
    /// been failing validation on every call — so in practice no interaction ever closed, and every verification
    /// ever recorded stayed live against its member indefinitely.</para>
    /// </summary>
    [SkippableFact]
    public async Task An_open_interaction_stops_being_verified_once_the_verification_ages_out()
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
                    TenantId = tenant, VerifiedIdentifierTypes = ["MemberNo", "DateOfBirth"],
                    Result = VerificationResult.Passed,
                    // Recorded just inside the window.
                    VerifiedAt = DateTimeOffset.UtcNow - VerificationService.VerificationTtl + TimeSpan.FromMinutes(5),
                });
                await db.SaveChangesAsync();
                interactionId = i.InteractionId;
            }

            await using (var db = new CallCentreDbContext(options))
            {
                (await new VerificationService(db).IsVerifiedAsync(interactionId, beneficiary))
                    .Should().BeTrue("the verification is still inside its TTL");
            }

            // Age it past the TTL. The interaction is untouched and still Open — only time has passed.
            await using (var db = new CallCentreDbContext(options))
            {
                var v = await db.Verifications.FirstAsync(x => x.InteractionId == interactionId);
                v.VerifiedAt = DateTimeOffset.UtcNow - VerificationService.VerificationTtl - TimeSpan.FromMinutes(1);
                await db.SaveChangesAsync();
            }

            await using (var db = new CallCentreDbContext(options))
            {
                var i = await db.Interactions.AsNoTracking().FirstAsync(x => x.InteractionId == interactionId);
                i.Status.Should().Be(InteractionStatus.Open, "the expiry must not depend on the call being closed");

                (await new VerificationService(db).IsVerifiedAsync(interactionId, beneficiary))
                    .Should().BeFalse("a verification older than the TTL is no longer evidence of who was on the line");
            }
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
