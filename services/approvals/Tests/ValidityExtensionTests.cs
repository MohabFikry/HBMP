using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Approvals.Api;
using Mersal.Approvals.Domain;

namespace Mersal.Approvals.Tests;

/// <summary>
/// A pharmacist or technician asking for an expired item to be revalidated.
/// </summary>
/// <remarks>
/// <para>
/// Two things are pinned. The requester may raise this ONE shape of question and nothing else — they hold
/// <c>auth:request-extension</c> and no decision scope, so the narrowness is the whole design and has to be
/// provable. And an approval that cannot reach the service owning the expired item is REFUSED rather than
/// recorded: an authorization saying Approved beside a prescription the counter still cannot dispense tells
/// the pharmacist yes on one screen and no on the next, with nothing on either to explain it.
/// </para>
/// </remarks>
[Collection("approvals-db")]
public class ValidityExtensionTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly Guid Item = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Beneficiary = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    [SkippableFact]
    public async Task A_pharmacist_can_raise_one_and_it_lands_in_the_approval_queue()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            using var pharmacist = app.PharmacistClient();
            var created = await Raise(pharmacist, Item);

            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var body = await created.Content.ReadFromJsonAsync<JsonElement>(Web);

            // Submitted, not Approved: raising is asking, and the requester decides nothing.
            body.GetProperty("status").GetString().Should().Be(nameof(AuthStatus.Submitted));
            body.GetProperty("authNo").GetString().Should().StartWith("AUTH-");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_requester_holds_no_decision_authority()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            using var pharmacist = app.PharmacistClient();
            var created = await Raise(pharmacist, Item);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("authorizationId").GetGuid();

            // The point of a purpose-built scope: they can ask, and that is the end of what they can do.
            // Approving their own request would make the whole routing to the approval team decorative.
            var approve = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/authorizations/{id}/approve", UriKind.Relative))
            {
                Content = JsonContent.Create(new { rationale = "self-approved" }, options: Web),
            };
            approve.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

            (await pharmacist.SendAsync(approve)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_second_request_for_the_same_item_is_refused_while_one_is_open()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            using var pharmacist = app.PharmacistClient();
            (await Raise(pharmacist, Item)).StatusCode.Should().Be(HttpStatusCode.Created);

            // A counter that gets no answer in a minute raises another, and another. Without this the
            // approval team works one question three times while the pharmacist watches three Submitted rows.
            var again = await Raise(pharmacist, Item);
            again.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await again.Content.ReadFromJsonAsync<JsonElement>(Web))
                .GetProperty("detail").GetString().Should().Contain("already with the approval team");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_request_with_no_reason_is_refused()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            using var pharmacist = app.PharmacistClient();

            // The reason IS the request. An approver deciding "should this patient get another ten days"
            // with an empty box in front of them is deciding on who asked, not on why.
            var empty = await Raise(pharmacist, Item, reason: "  ");
            empty.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

            var terse = await Raise(pharmacist, Item, reason: "pls");
            terse.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_approval_that_cannot_be_applied_is_not_recorded()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory { Applier = new RefusingApplier() };
        try
        {
            using var pharmacist = app.PharmacistClient();
            var created = await Raise(pharmacist, Item);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("authorizationId").GetGuid();

            using var reviewer = app.ReviewerClient();
            // Picked up first: Submitted → UnderReview is the normal path, and a decision straight off the
            // queue is refused by the state machine regardless of what it is about.
            await Assign(reviewer, id);
            var approve = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/authorizations/{id}/approve", UriKind.Relative))
            {
                Content = JsonContent.Create(new { rationale = "Patient still on the same course." }, options: Web),
            };
            approve.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var decided = await reviewer.SendAsync(approve);

            // 502, and the reviewer is told it can be retried — because nothing happened.
            decided.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            (await decided.Content.ReadAsStringAsync()).Should().Contain("has NOT been recorded");

            // And the authorization really is untouched, so a retry is a first attempt rather than a repair.
            var still = await reviewer.GetAsync(new Uri($"/api/v1/authorizations/{id}", UriKind.Relative));
            if (still.StatusCode == HttpStatusCode.OK)
            {
                (await still.Content.ReadFromJsonAsync<JsonElement>(Web))
                    .GetProperty("status").GetString().Should().Be(nameof(AuthStatus.UnderReview));
            }
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_rejection_lands_even_when_the_owning_service_is_unreachable()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory { Applier = new RefusingApplier() };
        try
        {
            using var pharmacist = app.PharmacistClient();
            var created = await Raise(pharmacist, Item);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("authorizationId").GetGuid();

            using var reviewer = app.ReviewerClient();
            await Assign(reviewer, id);
            var reject = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/authorizations/{id}/reject", UriKind.Relative))
            {
                Content = JsonContent.Create(new { rationale = "The original indication no longer applies." }, options: Web),
            };
            reject.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

            // There is nothing to apply for a NO, so an unreachable pharmacy must not block it. A rejection
            // that cannot be recorded leaves the request sitting in the queue looking undecided.
            (await reviewer.SendAsync(reject)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally { await app.CleanupAsync(); }
    }

    private static async Task Assign(HttpClient reviewer, Guid id)
    {
        var assign = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/authorizations/{id}/assign", UriKind.Relative));
        assign.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await reviewer.SendAsync(assign)).IsSuccessStatusCode.Should().BeTrue();
    }

    private static Task<HttpResponseMessage> Raise(HttpClient client, Guid itemId, string? reason = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/authorizations/validity-extensions", UriKind.Relative))
        {
            Content = JsonContent.Create(new RequestValidityExtensionRequest(
                "Prescription", itemId, "RX-2026-000312", Beneficiary,
                DateTimeOffset.UtcNow.AddDays(-2),
                reason ?? "Patient is mid-course and could not collect before it lapsed."), options: Web),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(req);
    }

    /// <summary>Stands in for pharmacy-service being down, refusing, or unreachable.</summary>
    private sealed class RefusingApplier : IValidityExtensionApplier
    {
        public Task<ExtensionOutcome> ApplyAsync(Mersal.Approvals.Domain.Authorization auth, string? bearerToken, CancellationToken ct = default)
            => Task.FromResult(ExtensionOutcome.Failed("pharmacy-service could not be reached, so the extension was not applied."));
    }
}

/// <summary>
/// The callback stubbed out to succeed, for the decision tests that are not about it.
///
/// <para>It lives in the TEST project rather than beside the interface because a stand-in that has to invent
/// a timestamp cannot do so with a bare <c>UtcNow</c> in production code — the clock guard in
/// <c>Mersal.Time.Tests</c> refuses it, and rightly: an injected clock is what makes a date testable, and a
/// bare one gives the wrong DATE every Cairo evening. Here the fixed instant is honest about being a fixture.</para>
/// </summary>
public sealed class NoopValidityExtensionApplier : IValidityExtensionApplier
{
    private static readonly DateTimeOffset Fixed = new(2026, 8, 14, 21, 0, 0, TimeSpan.Zero);

    public Task<ExtensionOutcome> ApplyAsync(
        Mersal.Approvals.Domain.Authorization auth, string? bearerToken, CancellationToken ct = default)
        => Task.FromResult(ExtensionOutcome.Ok(Fixed));
}
