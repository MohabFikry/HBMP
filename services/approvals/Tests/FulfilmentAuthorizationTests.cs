using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Approvals.Api;
using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;
using Mersal.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Approvals.Tests;

/// <summary>
/// The fulfilment authorization: what a counter handed over, recorded apart from the prescription (ADR-0034).
/// </summary>
/// <remarks>
/// <para>
/// The invariant these tests exist for is one sentence: <b>a substitution changes the authorization and never
/// the clinical record</b>. It is enforced structurally — the item stores what was WRITTEN and what was
/// DELIVERED in two different columns, and approvals-service has no client for the prescription at all — so
/// what is provable here is that the two facts really do both survive, and that a replayed dispense cannot
/// invent a second one.
/// </para>
/// <para>
/// The second is that settled work never enters the review queue. A fulfilment is born <c>Issued</c>, no
/// transition targets that status and none leaves it, and the worklist defaults to <c>Review</c> — because a
/// few hundred dispenses a day in the reviewer's inbox is a queue people stop reading.
/// </para>
/// </remarks>
[Collection("approvals-db")]
public class FulfilmentAuthorizationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly Guid Beneficiary = Guid.Parse("bbbbbbbb-0f00-0000-0000-000000000001");
    private static readonly Guid Provider = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private const string OrderedDrug = "d-augmentin-1g";
    private const string DeliveredDrug = "d-amox-clav-generic";

    // ---------------------------------------------------------------- validation (no DB needed)

    [Theory]
    [InlineData(null, "Prescription", "rx-1", "no tenant on the envelope")]
    [InlineData("t1", "Manual", "rx-1", "source must be Prescription or OrderLine")]
    [InlineData("t1", "Prescription", null, "no sourceRef")]
    public void A_message_that_cannot_be_trusted_is_named_rather_than_guessed_at(
        string? tenant, string source, string? sourceRef, string expected)
    {
        // Dead-lettered, not requeued and not coerced. An authorization stamped with a guessed tenant, or
        // hung off an invented source, is worse than none at all — it LOOKS like a record.
        var msg = Message(tenant, source, sourceRef);
        FulfilmentIssuer.Validate(msg).Should().Contain(expected);
    }

    [Fact]
    public void A_substituted_item_with_no_reason_is_refused()
    {
        // The DB enforces it too. Both, because a substitution with no stated reason is a molecule the
        // prescriber did not choose and no account of why — and a message that got this far without one is a
        // producer bug worth naming rather than a row worth writing.
        var msg = Message("t1", "Prescription", "rx-1") with
        {
            Items = [Item("f-1", OrderedDrug, DeliveredDrug, reason: null)],
        };
        FulfilmentIssuer.Validate(msg).Should().Contain("no reason");
    }

    [Fact]
    public void A_message_that_can_be_trusted_passes()
    {
        FulfilmentIssuer.Validate(Message("t1", "Prescription", "rx-1")).Should().BeNull();
    }

    // ---------------------------------------------------------------- issuance (DB)

    [SkippableFact]
    public async Task A_dispense_issues_an_authorization_that_keeps_BOTH_the_written_and_the_delivered_drug()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            var rx = Guid.NewGuid().ToString();
            var result = await IssueAsync(app, Message(app.Tenant, "Prescription", rx) with
            {
                Items = [Item("f-1", OrderedDrug, DeliveredDrug, "Prescribed brand is out of stock this morning.")],
            });

            result.Outcome.Should().Be(FulfilmentOutcome.Issued);
            result.AuthNo.Should().StartWith("AUTH-");

            await using var db = ApprovalsApiFactory.Ctx();
            var item = await db.Items.AsNoTracking().SingleAsync(i => i.AuthorizationId == result.AuthorizationId);

            // BOTH. The prescribed molecule is not overwritten by the dispensed one — that is the entire
            // reason the authorization is a separate document, and the fact a later reviewer most needs.
            item.OrderedCode.Should().Be(OrderedDrug);
            item.FulfilledCode.Should().Be(DeliveredDrug);
            item.Substituted.Should().BeTrue();
            item.SubstitutionReason.Should().NotBeNullOrWhiteSpace();
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task It_is_born_Issued_and_a_reviewer_cannot_pick_it_up()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            var rx = Guid.NewGuid().ToString();
            var result = await IssueAsync(app, Message(app.Tenant, "Prescription", rx));

            await using var db = ApprovalsApiFactory.Ctx();
            var auth = await db.Authorizations.AsNoTracking().SingleAsync(a => a.AuthorizationId == result.AuthorizationId);
            auth.Kind.Should().Be(AuthKind.Fulfilment);
            auth.Status.Should().Be(AuthStatus.Issued);

            // Nothing to approve: the medicine is already in the patient's hand. Assigning it would start an
            // SLA clock on a question nobody asked, so the state machine admits no way in or out.
            AuthorizationWorkflow.CanTransition(AuthStatus.Issued, AuthStatus.UnderReview).Should().BeFalse();
            AuthorizationWorkflow.CanTransition(AuthStatus.Submitted, AuthStatus.Issued).Should().BeFalse();

            using var reviewer = app.ReviewerClient();
            var assign = await reviewer.PostAsync($"/api/v1/authorizations/{result.AuthorizationId}/assign", null);
            assign.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_second_dispense_against_the_same_prescription_appends_rather_than_issuing_again()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            var rx = Guid.NewGuid().ToString();
            var first = await IssueAsync(app, Message(app.Tenant, "Prescription", rx) with { Items = [Item("f-1")] });
            var second = await IssueAsync(app, Message(app.Tenant, "Prescription", rx) with { Items = [Item("f-2")] });

            // A member collecting a fortnight's medication over two visits has ONE authorization with two
            // items — not two authorizations that whoever reads them has to add up.
            second.Outcome.Should().Be(FulfilmentOutcome.Appended);
            second.AuthorizationId.Should().Be(first.AuthorizationId);

            await using var db = ApprovalsApiFactory.Ctx();
            (await db.Items.AsNoTracking().CountAsync(i => i.AuthorizationId == first.AuthorizationId)).Should().Be(2);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_redelivered_dispense_cannot_post_twice()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            var rx = Guid.NewGuid().ToString();
            var msg = Message(app.Tenant, "Prescription", rx) with { Items = [Item("f-1")] };
            await IssueAsync(app, msg);

            // At-least-once delivery means this happens. The processed-event ledger catches a redelivered
            // MESSAGE id; this guard — the UNIQUE fulfilment_ref — is the one that survives a redelivery
            // arriving under a NEW message id, which the ledger has never seen.
            var replay = await IssueAsync(app, msg);
            replay.Outcome.Should().Be(FulfilmentOutcome.Duplicate);

            await using var db = ApprovalsApiFactory.Ctx();
            (await db.Items.AsNoTracking().CountAsync(i => i.AuthorizationId == replay.AuthorizationId)).Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    // ---------------------------------------------------------------- the approval team's view (DB)

    [SkippableFact]
    public async Task The_reviewer_inbox_does_not_fill_up_with_dispenses_but_the_register_shows_them()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            var rx = Guid.NewGuid().ToString();
            var issued = await IssueAsync(app, Message(app.Tenant, "Prescription", rx) with
            {
                Items = [Item("f-1", OrderedDrug, DeliveredDrug, "Out of stock this morning.")],
            });

            using var reviewer = app.ReviewerClient();

            // The DEFAULT is the work queue. A few hundred dispenses a day landing in it would drown the
            // handful of requests that need a decision, and a queue that is mostly noise stops being read.
            var inbox = await reviewer.GetFromJsonAsync<JsonElement>("/api/v1/authorizations/", Web);
            inbox.EnumerateArray().Select(x => x.GetProperty("authorizationId").GetGuid())
                .Should().NotContain(issued.AuthorizationId!.Value);

            // The register is asked for deliberately, and it is there.
            var register = await reviewer.GetFromJsonAsync<JsonElement>("/api/v1/authorizations/?kind=Fulfilment", Web);
            var row = register.EnumerateArray()
                .Single(x => x.GetProperty("authorizationId").GetGuid() == issued.AuthorizationId!.Value);
            row.GetProperty("kind").GetString().Should().Be("Fulfilment");
            // The reference a human can actually look up — an authorization with no trace of what it was
            // issued against is a number with nothing behind it.
            row.GetProperty("itemReference").GetString().Should().Be("RX-2026-000410");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_items_endpoint_shows_written_and_delivered_side_by_side_and_no_clinical_payload()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            var rx = Guid.NewGuid().ToString();
            var issued = await IssueAsync(app, Message(app.Tenant, "Prescription", rx) with
            {
                Items = [Item("f-1", OrderedDrug, DeliveredDrug, "Out of stock this morning.")],
            });

            using var reviewer = app.ReviewerClient();
            var items = await reviewer.GetFromJsonAsync<JsonElement>(
                $"/api/v1/authorizations/{issued.AuthorizationId}/items", Web);

            var item = items.EnumerateArray().Single();
            item.GetProperty("orderedCode").GetString().Should().Be(OrderedDrug);
            item.GetProperty("fulfilledCode").GetString().Should().Be(DeliveredDrug);
            item.GetProperty("substituted").GetBoolean().Should().BeTrue();
            item.GetProperty("substitutionReason").GetString().Should().NotBeNullOrWhiteSpace();

            // The bounded exception, and the boundary. A substitution reason is logistics written by a
            // pharmacist and is the entire substance of the decision; a diagnosis is not, and there is no
            // field here that could carry one.
            var names = item.EnumerateObject().Select(p => p.Name).ToList();
            names.Should().NotContain(n =>
                n.Contains("diagnos", StringComparison.OrdinalIgnoreCase)
                || n.Contains("note", StringComparison.OrdinalIgnoreCase)
                || n.Contains("indication", StringComparison.OrdinalIgnoreCase));
        }
        finally { await app.CleanupAsync(); }
    }

    // ---------------------------------------------------------------- the bench's substitution question (DB)

    [SkippableFact]
    public async Task A_technician_asking_about_a_different_examination_raises_a_review_not_a_fulfilment()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            using var tech = app.TechnicianClient();
            var line = Guid.NewGuid();
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/authorizations/substitution-requests")
            {
                Content = JsonContent.Create(new
                {
                    orderId = Guid.NewGuid(), orderLineId = line, orderReference = "ORD-2026-055012",
                    beneficiaryId = Beneficiary, orderedCode = "70551", orderedLabel = "MRI Brain",
                    reason = "The contrast scanner is out of service until Thursday and the patient travelled today.",
                }, options: Web),
            };
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var created = await tech.SendAsync(req);

            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var body = await created.Content.ReadFromJsonAsync<JsonElement>(Web);
            // Submitted, not Issued: the technician is ASKING. Nothing has been delivered and nothing is
            // authorized until somebody qualified answers.
            body.GetProperty("status").GetString().Should().Be(nameof(AuthStatus.Submitted));
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_empty_reason_is_refused_because_it_is_the_whole_decision()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            using var tech = app.TechnicianClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/authorizations/substitution-requests")
            {
                Content = JsonContent.Create(new
                {
                    orderId = Guid.NewGuid(), orderLineId = Guid.NewGuid(), orderReference = "ORD-1",
                    beneficiaryId = Beneficiary, orderedCode = "70551", reason = "busy",
                }, options: Web),
            };
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

            // An approver with an empty box decides on who asked rather than on why, and unlike a dispensing
            // substitution there is no formulary anyone downstream could infer the answer from.
            (await tech.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        }
        finally { await app.CleanupAsync(); }
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<FulfilmentResult> IssueAsync(ApprovalsApiFactory app, FulfilmentMessage msg)
    {
        app.CreateClient();   // realise the host
        using var scope = app.Services.CreateScope();
        // The consumer binds the RLS tenant from the envelope because it has no HTTP principal; so does this.
        scope.ServiceProvider.GetRequiredService<RlsContext>().TenantId = msg.TenantId!;
        return await scope.ServiceProvider.GetRequiredService<FulfilmentIssuer>().IssueAsync(msg);
    }

    private static FulfilmentMessage Message(string? tenant, string source, string? sourceRef) => new(
        tenant, Beneficiary, Provider, EncounterId: null, source, sourceRef,
        SourceNo: "RX-2026-000410", BenefitCategory: "PHARMACY", ActorUserId: "pharmacist-1",
        FulfilledAt: new DateTimeOffset(2026, 8, 4, 9, 0, 0, TimeSpan.Zero),
        Items: [Item("f-1")]);

    private static FulfilmentItemMessage Item(
        string reference, string ordered = OrderedDrug, string? delivered = null, string? reason = null) =>
        new(reference, Guid.NewGuid(), ordered, "Augmentin 1g", delivered ?? ordered,
            delivered is null ? "Augmentin 1g" : "Amoxicillin+Clavulanic acid 1g", 14m, reason);
}
