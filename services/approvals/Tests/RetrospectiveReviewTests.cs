using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Approvals.Api;
using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Authorization = Mersal.Approvals.Domain.Authorization;

namespace Mersal.Approvals.Tests;

/// <summary>
/// The break-glass retrospective review — the control that was declared and never implemented.
/// </summary>
/// <remarks>
/// <para>Emergency approval, director override and manual authorization all set
/// <c>RetrospectiveReviewRequired</c> and the queue endpoint has served the open ones since phase 7.3. Nothing
/// ever closed one: before migration 0016, <c>RetrospectiveReviewed</c> appeared in exactly two places in the
/// repository — its own declaration and the <c>NOT</c> predicate that reads it. No endpoint, service or job
/// assigned it. The queue was write-only.</para>
/// <para>That is not a missing feature; it is the reason break-glass is defensible. An override is acceptable
/// BECAUSE somebody checks it afterwards, and the flag as it stood recorded that a review was owed and never
/// that one happened — so the trail could not tell "reviewed and upheld" from "nobody ever looked".</para>
/// <para>The refusals below are the substance. A review that anybody can record, about anything, with no
/// reasoning, signed by the person who took the decision, is the checkbox this replaced.</para>
/// </remarks>
[Collection("approvals-db")]
public sealed class RetrospectiveReviewTests : IAsyncLifetime, IDisposable
{
    private readonly ApprovalsApiFactory factory = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await factory.CleanupAsync();
    public void Dispose() => factory.Dispose();

    /// <summary>The director holds the review; nothing else about the client changes.</summary>
    private HttpClient Reviewer() => factory.As(
        ApprovalsTestAuth.DirectorSub, "medical_director",
        "auth:read auth:list auth:review auth:decide auth:emergency auth:manual auth:retrospective");

    [SkippableFact]
    public async Task A_break_glass_case_is_reviewed_and_leaves_the_queue()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var id = await SeedBreakGlassAsync(actor: Guid.Parse(ApprovalsTestAuth.ReviewerSub));
        var client = Reviewer();

        (await OpenQueueAsync(client)).Should().Contain(id, "a break-glass decision is owed a review");

        var res = await client.PostAsJsonAsync($"/api/v1/authorizations/{id}/retrospective-review",
            new { outcome = "Upheld", rationale = "Member present, provider systems offline; the service was covered." });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        (await OpenQueueAsync(client)).Should().NotContain(id, "a reviewed case has left the open queue");
        (await ClosedQueueAsync(client)).Should().Contain(id, "and appears in the reviewed half");

        await using var db = ApprovalsApiFactory.Ctx();
        var auth = await db.Authorizations.AsNoTracking().SingleAsync(a => a.AuthorizationId == id);
        auth.RetrospectiveReviewed.Should().BeTrue();
        auth.RetrospectiveOutcome.Should().Be("Upheld");
        auth.RetrospectiveReviewedBy.Should().Be(ApprovalsTestAuth.DirectorSub);
        auth.RetrospectiveReviewedAt.Should().NotBeNull();
        auth.RetrospectiveRationale.Should().NotBeNullOrWhiteSpace();
    }

    [SkippableFact]
    public async Task The_actor_may_not_review_their_own_break_glass_decision()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        // The break-glass decision is attributed to the DIRECTOR, who is also the caller below. Somebody
        // signing off their own override is the precise failure this control exists to catch, and the role
        // split alone does not stop it: a director reviewing another director's override is legitimate.
        var id = await SeedBreakGlassAsync(actor: Guid.Parse(ApprovalsTestAuth.DirectorSub));

        var res = await Reviewer().PostAsJsonAsync($"/api/v1/authorizations/{id}/retrospective-review",
            new { outcome = "Upheld", rationale = "Looks fine to me." });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("SOD_SELF_RETROSPECTIVE_REVIEW");

        await using var db = ApprovalsApiFactory.Ctx();
        var auth = await db.Authorizations.AsNoTracking().SingleAsync(a => a.AuthorizationId == id);
        auth.RetrospectiveReviewed.Should().BeFalse("a refused review must leave the case open");
    }

    [SkippableFact]
    public async Task A_review_without_a_rationale_is_refused()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var id = await SeedBreakGlassAsync(actor: Guid.Parse(ApprovalsTestAuth.ReviewerSub));

        // A review that records no reasoning is not a review; it is a checkbox, which is what this control
        // already effectively was.
        var res = await Reviewer().PostAsJsonAsync($"/api/v1/authorizations/{id}/retrospective-review",
            new { outcome = "Upheld", rationale = "   " });

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await OpenQueueAsync(Reviewer())).Should().Contain(id);
    }

    [SkippableFact]
    public async Task An_authorization_that_was_not_break_glass_has_nothing_to_review()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var id = await SeedBreakGlassAsync(actor: Guid.Parse(ApprovalsTestAuth.ReviewerSub), breakGlass: false);

        var res = await Reviewer().PostAsJsonAsync($"/api/v1/authorizations/{id}/retrospective-review",
            new { outcome = "Upheld", rationale = "Nothing to see." });

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [SkippableFact]
    public async Task A_case_cannot_be_reviewed_twice()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var id = await SeedBreakGlassAsync(actor: Guid.Parse(ApprovalsTestAuth.ReviewerSub));
        var client = Reviewer();

        var first = await client.PostAsJsonAsync($"/api/v1/authorizations/{id}/retrospective-review",
            new { outcome = "NotJustified", rationale = "No emergency was documented at the time." });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.PostAsJsonAsync($"/api/v1/authorizations/{id}/retrospective-review",
            new { outcome = "Upheld", rationale = "On reflection." });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict, "a completed review is a record, not a draft");

        await using var db = ApprovalsApiFactory.Ctx();
        var auth = await db.Authorizations.AsNoTracking().SingleAsync(a => a.AuthorizationId == id);
        auth.RetrospectiveOutcome.Should().Be("NotJustified", "the first conclusion stands");
    }

    [SkippableFact]
    public async Task NotJustified_records_a_finding_and_does_not_reverse_the_authorization()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var id = await SeedBreakGlassAsync(actor: Guid.Parse(ApprovalsTestAuth.ReviewerSub));

        var res = await Reviewer().PostAsJsonAsync($"/api/v1/authorizations/{id}/retrospective-review",
            new { outcome = "NotJustified", rationale = "The member could have been seen the following morning." });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = ApprovalsApiFactory.Ctx();
        var auth = await db.Authorizations.AsNoTracking().SingleAsync(a => a.AuthorizationId == id);
        // The care was delivered under this authorization. Unwinding it retroactively would refuse a service
        // that has already happened, to a beneficiary who had no part in the decision. The finding is the
        // output — it is what an oversight report is built from and what a conversation with the decider
        // starts from, not a reversal.
        auth.Status.Should().Be(AuthStatus.EmergencyApproved);
        auth.RetrospectiveOutcome.Should().Be("NotJustified");
    }

    [SkippableFact]
    public async Task The_approval_team_may_not_complete_a_review()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var id = await SeedBreakGlassAsync(actor: Guid.Parse(ApprovalsTestAuth.DirectorSub));

        // `medical_approval` holds auth:manual and auth:emergency — they RAISE break-glass authorizations.
        // Granting them the review too would make one team both actor and auditor as a class, which the
        // per-person SoD above does not cover: it stops somebody reviewing their own, not a team its own.
        var res = await factory.ReviewerClient().PostAsJsonAsync(
            $"/api/v1/authorizations/{id}/retrospective-review",
            new { outcome = "Upheld", rationale = "Fine." });

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    // ---------------------------------------------------------------------------------------------------

    private async Task<Guid> SeedBreakGlassAsync(Guid actor, bool breakGlass = true)
    {
        await using var db = ApprovalsApiFactory.Ctx();
        var now = DateTimeOffset.UtcNow;
        var auth = new Authorization
        {
            AuthorizationId = Guid.NewGuid(),
            AuthNo = await new AuthNoIssuer(db).NextAsync(2026),
            TenantId = factory.Tenant,
            BeneficiaryId = Guid.NewGuid(),
            Source = AuthSource.Manual,
            ServiceCodes = "[\"70553\"]",
            RequestedScope = "{}",
            Priority = AuthPriority.Emergency,
            Status = breakGlass ? AuthStatus.EmergencyApproved : AuthStatus.Approved,
            SubmittedAt = now.AddDays(-40), DecidedAt = now.AddDays(-40),
            CreatedAt = now.AddDays(-40), UpdatedAt = now.AddDays(-40),
            TatSeconds = 0,
            RetrospectiveReviewRequired = breakGlass,
        };
        auth.Decisions.Add(new AuthorizationDecision
        {
            DecisionId = Guid.NewGuid(), AuthorizationId = auth.AuthorizationId, TenantId = factory.Tenant,
            Decision = breakGlass ? AuthDecision.EmergencyApproved : AuthDecision.Approved,
            ReviewerId = actor, DecidedAt = now.AddDays(-40), BreakGlass = breakGlass,
            Justification = breakGlass ? "member present, provider offline" : null,
        });
        db.Authorizations.Add(auth);
        await db.SaveChangesAsync();
        return auth.AuthorizationId;
    }

    private static async Task<IReadOnlyList<Guid>> OpenQueueAsync(HttpClient c) => await QueueAsync(c, closed: false);
    private static async Task<IReadOnlyList<Guid>> ClosedQueueAsync(HttpClient c) => await QueueAsync(c, closed: true);

    private static async Task<IReadOnlyList<Guid>> QueueAsync(HttpClient c, bool closed)
    {
        var res = await c.GetAsync($"/api/v1/authorizations/retrospective-queue{(closed ? "?closed=true" : "")}");
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return [.. doc.RootElement.EnumerateArray().Select(e => e.GetProperty("authorizationId").GetGuid())];
    }
}
