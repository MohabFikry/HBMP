using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Approvals.Api;
using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Approvals.Tests;

/// <summary>
/// A replayed <c>Idempotency-Key</c> answers the request it replays, or it answers nothing.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> <c>Decisions.Decide</c> looked the key up in <c>processed_request</c> and, on a
/// hit, returned whatever decision that key had produced — without ever comparing the body. So a REJECT
/// retried under a key already used for an APPROVE came back <c>200 OK, approved</c>. The reviewer is told
/// the opposite of what they asked for; the authorization really is approved; the appeal route the rejection
/// was supposed to open never opens; and nothing anywhere records the disagreement. There is no error to
/// investigate, because from the platform's side nothing went wrong.</para>
/// <para>18.A3 settled this rule for the consume and dispense paths — store a hash of the canonical request
/// beside the key, refuse a replay whose hash differs. The approvals ledger had no column to apply it with
/// until migration 0011.</para>
/// </remarks>
[Collection("approvals-db")]
public class DecisionReplayIsBoundToTheBodyTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task A_reject_retried_under_an_approves_key_is_refused_rather_than_answered_approved()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            var id = await SeedUnderReviewAsync(app);
            using var reviewer = app.ReviewerClient();
            var key = Guid.NewGuid().ToString();

            var approved = await PostAsync(reviewer, $"/api/v1/authorizations/{id}/approve", key,
                new ApproveRequest("within policy"));
            approved.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await approved.Content.ReadAsStringAsync());

            var reject = await PostAsync(reviewer, $"/api/v1/authorizations/{id}/reject", key,
                new RejectRequest("out of policy after all"));

            reject.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity,
                "answering a rejection with the earlier approval reports a verdict the reviewer did not give");
            (await reject.Content.ReadAsStringAsync()).Should().Contain("idempotency-key-reuse");

            // And nothing moved: the approval stands, one decision on the ledger, because a refusal that
            // half-applied would be worse than the defect.
            await using var db = ApprovalsApiFactory.Ctx();
            var auth = await db.Authorizations.AsNoTracking().Include(a => a.Decisions)
                .SingleAsync(a => a.AuthorizationId == id);
            auth.Status.Should().Be(AuthStatus.Approved);
            auth.Decisions.Should().ContainSingle();
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_SAME_decision_retried_under_the_same_key_still_replays()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            var id = await SeedUnderReviewAsync(app);
            using var reviewer = app.ReviewerClient();
            var key = Guid.NewGuid().ToString();
            var body = new ApproveRequest("within policy");

            (await PostAsync(reviewer, $"/api/v1/authorizations/{id}/approve", key, body))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            // The point of the header, and it must survive the new check: a client that retries after a
            // dropped response gets the decision it already made, not a second one and not a 422.
            var replay = await PostAsync(reviewer, $"/api/v1/authorizations/{id}/approve", key, body);
            replay.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await replay.Content.ReadAsStringAsync());

            await using var db = ApprovalsApiFactory.Ctx();
            (await db.Set<AuthorizationDecision>().CountAsync(d => d.AuthorizationId == id)).Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_changed_RATIONALE_is_a_different_request_too()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            var id = await SeedUnderReviewAsync(app);
            using var reviewer = app.ReviewerClient();
            var key = Guid.NewGuid().ToString();

            (await PostAsync(reviewer, $"/api/v1/authorizations/{id}/approve", key, new ApproveRequest("within policy")))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            // The verdict is the same; the REASON is not. The rationale is the whole substance of the
            // decision record — it is what an appeal argues with — so a replay that silently kept the first
            // one would leave the reviewer believing they had corrected a record they had not.
            var corrected = await PostAsync(reviewer, $"/api/v1/authorizations/{id}/approve", key,
                new ApproveRequest("approved on the consultant's advice"));

            corrected.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static async Task<Guid> SeedUnderReviewAsync(ApprovalsApiFactory app)
    {
        app.CreateClient();   // realise the host
        await using var db = ApprovalsApiFactory.Ctx();
        var auth = new Mersal.Approvals.Domain.Authorization
        {
            AuthorizationId = Guid.NewGuid(), AuthNo = await new AuthNoIssuer(db).NextAsync(2026),
            TenantId = app.Tenant,
            BeneficiaryId = Guid.NewGuid(), Source = AuthSource.OrderLine, RequestingProviderId = Guid.NewGuid(),
            ServiceCodes = "[\"70450\"]", Status = AuthStatus.UnderReview,
            SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-10), SlaDueAt = DateTimeOffset.UtcNow.AddHours(1),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Authorizations.Add(auth);
        await db.SaveChangesAsync();
        return auth.AuthorizationId;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, string? idempotencyKey, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Relative))
        {
            Content = JsonContent.Create(body, body.GetType(), options: Web),
        };
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(req);
    }
}
