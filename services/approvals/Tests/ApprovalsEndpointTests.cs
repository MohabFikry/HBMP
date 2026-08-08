using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Approvals.Api;
using Mersal.Approvals.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Approvals.Tests;

/// <summary>
/// Phase 24 Gate 3 — who may decide an authorization, over HTTP.
///
/// <para>The decision RULES are well covered below HTTP. What was not covered is the part that decides who
/// reaches them: the break-glass paths that admit only a medical director, the mandatory rejection reason,
/// and the partial-approval scope check that stops "partially approved" being used to approve everything
/// while reading as a narrowed decision in every report.</para>
/// </summary>
[Collection("approvals-db")]
public class ApprovalsEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string[] Requested = ["99213", "80053", "71046"];

    /// <summary>
    /// A manual authorization is break-glass: it creates an APPROVED authorization with no request behind it.
    /// Only a medical director may, and only with a justification — the reviewer holds auth:decide and is
    /// still refused, because deciding a request and inventing one are different powers.
    /// </summary>
    [SkippableFact]
    public async Task Only_a_medical_director_may_raise_a_manual_authorization_and_only_with_a_justification()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            using var reviewer = app.ReviewerClient();
            var refused = await PostAsync(reviewer, "/api/v1/authorizations/manual", Guid.NewGuid().ToString(),
                Manual(justification: "urgent"));
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "holding auth:decide is not holding auth:manual — deciding a request and inventing one are " +
                "different powers");

            using var director = app.DirectorClient();
            var noJustification = await PostAsync(director, "/api/v1/authorizations/manual",
                Guid.NewGuid().ToString(), Manual(justification: "   "));
            noJustification.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await noJustification.Content.ReadAsStringAsync()).Should().Contain("justification-required");

            var noKey = await PostAsync(director, "/api/v1/authorizations/manual", null, Manual("urgent"));
            noKey.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await noKey.Content.ReadAsStringAsync()).Should().Contain("missing-idempotency-key");

            var granted = await PostAsync(director, "/api/v1/authorizations/manual",
                Guid.NewGuid().ToString(), Manual("documented emergency"));
            granted.StatusCode.Should().Be(HttpStatusCode.Created, "{0}", await granted.Content.ReadAsStringAsync());

            await using var db = ApprovalsApiFactory.Ctx();
            (await db.Authorizations.CountAsync(a => a.TenantId == app.Tenant && a.Source == AuthSource.Manual))
                .Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>A retried manual authorization under the same key returns the first one. A duplicate here is
    /// a second standing approval for care that was authorized once.</summary>
    [SkippableFact]
    public async Task Replaying_a_manual_authorization_key_creates_no_second_authorization()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            using var director = app.DirectorClient();
            var key = Guid.NewGuid().ToString();
            var body = Manual("documented emergency");

            var first = await PostAsync(director, "/api/v1/authorizations/manual", key, body);
            first.StatusCode.Should().Be(HttpStatusCode.Created, "{0}", await first.Content.ReadAsStringAsync());
            var authId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizationId").GetGuid();

            // The replay answers 200, not 201: nothing was created this time, and the status says so.
            var replay = await PostAsync(director, "/api/v1/authorizations/manual", key, body);
            replay.StatusCode.Should().Be(HttpStatusCode.OK);
            (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizationId").GetGuid()
                .Should().Be(authId);

            await using var db = ApprovalsApiFactory.Ctx();
            (await db.Authorizations.CountAsync(a => a.TenantId == app.Tenant)).Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// A manual authorization must be a GRANT. Rejecting through the break-glass path would be a refusal with
    /// no request behind it and no appeal route — a denial nobody asked for and nobody can contest.
    /// </summary>
    [SkippableFact]
    public async Task A_manual_authorization_cannot_be_a_rejection()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            using var director = app.DirectorClient();
            var r = await PostAsync(director, "/api/v1/authorizations/manual", Guid.NewGuid().ToString(),
                Manual("documented emergency") with { Decision = AuthDecision.Rejected });
            r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await r.Content.ReadAsStringAsync()).Should().Contain("invalid-decision");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// The partial-approval scope check. "Partially approved" with the full requested set is a full approval
    /// wearing a narrower label — it reads as a restricted decision in every downstream report while granting
    /// everything, so it is refused rather than normalized.
    /// </summary>
    [SkippableFact]
    public async Task A_partial_approval_must_be_a_strict_non_empty_subset_of_what_was_requested()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            using var director = app.DirectorClient();

            var everything = await PostAsync(director, "/api/v1/authorizations/manual",
                Guid.NewGuid().ToString(),
                Manual("documented emergency") with
                {
                    Decision = AuthDecision.PartiallyApproved,
                    ApprovedScope = Requested,
                });
            everything.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await everything.Content.ReadAsStringAsync()).Should().Contain("invalid-approved-scope");

            var nothing = await PostAsync(director, "/api/v1/authorizations/manual",
                Guid.NewGuid().ToString(),
                Manual("documented emergency") with
                {
                    Decision = AuthDecision.PartiallyApproved,
                    ApprovedScope = [],
                });
            nothing.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

            var outsideTheRequest = await PostAsync(director, "/api/v1/authorizations/manual",
                Guid.NewGuid().ToString(),
                Manual("documented emergency") with
                {
                    Decision = AuthDecision.PartiallyApproved,
                    ApprovedScope = ["00000"],
                });
            outsideTheRequest.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity,
                "approving a code nobody asked for is not a narrowing of the request");

            var genuine = await PostAsync(director, "/api/v1/authorizations/manual",
                Guid.NewGuid().ToString(),
                Manual("documented emergency") with
                {
                    Decision = AuthDecision.PartiallyApproved,
                    ApprovedScope = [Requested[0]],
                });
            genuine.StatusCode.Should().Be(HttpStatusCode.Created, "{0}", await genuine.Content.ReadAsStringAsync());
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>A rejection reason is MANDATORY. A refusal with no recorded reason cannot be appealed, and the
    /// appeal is the beneficiary's only route.</summary>
    [SkippableFact]
    public async Task A_rejection_without_a_reason_is_refused()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            using var director = app.DirectorClient();
            var created = await PostAsync(director, "/api/v1/authorizations/manual",
                Guid.NewGuid().ToString(), Manual("documented emergency"));
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var authId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizationId").GetGuid();

            using var reviewer = app.ReviewerClient();
            var r = await reviewer.PostAsJsonAsync($"/api/v1/authorizations/{authId}/reject",
                new RejectRequest("   "), Web);
            r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await r.Content.ReadAsStringAsync()).Should().Contain("rejection-reason-required");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Deciding_an_authorization_that_does_not_exist_is_a_404()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        using var reviewer = app.ReviewerClient();
        // With a key, so the 404 is about the authorization and not about the missing header.
        (await PostAsync(reviewer, $"/api/v1/authorizations/{Guid.NewGuid()}/approve", Guid.NewGuid().ToString(),
            new ApproveRequest("looks fine")))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task An_unauthenticated_caller_reaches_nothing_and_the_programme_gate_refuses_a_tenant_that_is_off()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        using var anonymous = app.CreateClient();
        (await anonymous.GetAsync(new Uri("/api/v1/authorizations/retrospective-queue", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var offProgramme = app.As(ApprovalsTestAuth.DirectorSub, "medical_director",
            "auth:read auth:list auth:review auth:decide", features: Mersal.Authz.ProgramFeatures.Emr);
        (await offProgramme.GetAsync(new Uri("/api/v1/authorizations/retrospective-queue", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static ManualAuthorizationRequest Manual(string justification) => new(
        BeneficiaryId: Guid.NewGuid(), ServiceCodes: Requested, RequestedScope: null,
        Decision: AuthDecision.Approved, ApprovedScope: null, Justification: justification, Rationale: null);

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
