using FluentAssertions;
using Mersal.Approvals.Api;
using Mersal.Approvals.Domain;
using Mersal.Data;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Approvals.Tests;

/// <summary>
/// The FORWARD leg of the prior-authorization saga: a gated order or prescription reaches the reviewer.
/// </summary>
/// <remarks>
/// <para><b>The gap these close.</b> <c>POST /api/v1/authorizations</c> was written in phase 7 as the seam
/// "the phase-4 routing saga / the OrderPendingApproval|RxSubmitted event consumer" would call. No such
/// consumer existed and <c>auth:ingest</c> had no holder anywhere in the platform, so a gated order changed
/// status to PendingApproval, told the patient to wait, and reached nobody: the reviewer worklist only ever
/// contained requests a human had raised by hand.</para>
/// <para>Three things have to hold for the leg to be real, and each has a test here: the two events are on
/// the mirror so they arrive at all; an ungated prescription — the majority of the queue — produces nothing;
/// and a redelivery does not raise the same request twice.</para>
/// </remarks>
[Collection("approvals-db")]
public class RoutedIngestionTests
{
    private static readonly Guid Beneficiary = Guid.Parse("bbbbbbbb-0f00-0000-0000-000000000042");
    private static readonly Guid Provider = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Encounter = Guid.Parse("eeeeeeee-0000-0000-0000-000000000042");

    // ---------------------------------------------------------------- the mirror (no DB needed)

    [Fact]
    public void The_two_routing_events_are_mirrored_to_approvals()
    {
        // Without this the whole leg is unreachable however correct everything downstream is: the transport
        // is point-to-point and policy-service is already bound to both producer streams, so approvals gets
        // its own copy or it gets nothing.
        ApprovalRoutingFeed.Includes("OrderPendingApproval").Should().BeTrue();
        ApprovalRoutingFeed.Includes("RxSubmitted").Should().BeTrue();
        ApprovalRoutingFeed.Queue.Should().NotBe("orders.events").And.NotBe("pharmacy.events");
    }

    [Fact]
    public void Nothing_else_is_routed_for_a_decision()
    {
        // An allow-list, deliberately. `OrderActivated` and `RxApproved` are routing saying a decision was
        // NOT needed; raising an authorization for either would put settled work in the reviewer's queue.
        ApprovalRoutingFeed.EventTypes.Should().HaveCount(2);
        ApprovalRoutingFeed.Includes("OrderActivated").Should().BeFalse();
        ApprovalRoutingFeed.Includes("RxApproved").Should().BeFalse();
        ApprovalRoutingFeed.Includes("FulfilmentRecorded").Should().BeFalse();
    }

    // ---------------------------------------------------------------- validation (no DB needed)

    [Theory]
    [InlineData(null, "OrderPendingApproval", "no tenant on the envelope")]
    [InlineData("t1", "OrderCreated", "is not a routing event")]
    public void A_message_that_cannot_be_trusted_is_named_rather_than_guessed_at(
        string? tenant, string eventType, string expected)
    {
        // Dead-lettered, not requeued and not coerced. An authorization pointing at no real order looks to a
        // reviewer like a request somebody made — and approving it grants nothing.
        RoutedAuthorizationIngestor.Validate(eventType, OrderMessage(tenant)).Should().Contain(expected);
    }

    [Fact]
    public void An_order_event_with_no_order_id_is_refused()
    {
        RoutedAuthorizationIngestor.Validate("OrderPendingApproval", OrderMessage("t1") with { OrderId = null })
            .Should().Contain("no orderId");
    }

    [Fact]
    public void A_request_nobody_can_be_attributed_to_is_refused()
    {
        // Migration 0010's rule, named here rather than left to surface as a CHECK violation inside the
        // consumer — which would be an exception requeued five times before anyone saw a reason. An
        // authorization must be attributable to a provider that raised it or a person who did.
        RoutedAuthorizationIngestor.Validate(
            "RxSubmitted", RxMessage("t1") with { ProviderId = null, OrderedByUserId = null })
            .Should().Contain("nobody to attribute this to");
    }

    [Fact]
    public void A_beneficiaryless_message_is_refused()
    {
        // An authorization has to be ABOUT somebody. Without it the reviewer sees a request with no patient
        // and the decision cannot be attributed to any coverage.
        RoutedAuthorizationIngestor.Validate("RxSubmitted", RxMessage("t1") with { BeneficiaryId = Guid.Empty })
            .Should().Contain("no beneficiary");
    }

    // ---------------------------------------------------------------- ingestion (DB)

    [SkippableFact]
    public async Task A_gated_order_lands_on_the_reviewer_worklist_as_Submitted()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            var result = await IngestAsync(app, Guid.NewGuid(), "OrderPendingApproval", OrderMessage(app.Tenant));

            result.Outcome.Should().Be(RoutingOutcome.Raised);
            result.AuthNo.Should().StartWith("AUTH-");

            await using var db = ApprovalsApiFactory.Ctx();
            var auth = await db.Authorizations.AsNoTracking().SingleAsync(a => a.AuthorizationId == result.AuthorizationId);

            // A QUESTION, not a register entry: Review kind and Submitted status, which is what the default
            // worklist filter returns and what `assign` will accept.
            auth.Kind.Should().Be(AuthKind.Review);
            auth.Status.Should().Be(AuthStatus.Submitted);
            auth.Source.Should().Be(AuthSource.OrderLine);
            auth.SourceRef.Should().Be(OrderId.ToString());
            auth.EncounterId.Should().Be(Encounter);

            // The ordering clinician, not the machine that relayed the event. This is who the decision notice
            // is addressed to — with a service principal there the notice had no human to reach.
            auth.CreatedBy.Should().Be("dr-hana");

            // The requested codes, because a partial approval must be a strict subset of them. An
            // authorization ingested without codes can only be approved or rejected outright.
            System.Text.Json.JsonSerializer.Deserialize<string[]>(auth.ServiceCodes)
                .Should().BeEquivalentTo(["85025", "80053"]);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_ungated_prescription_raises_nothing()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            // RxSubmitted fires for EVERY prescription; the routing outcome is the flag, not the event name.
            // Raising an authorization for each would put a few hundred a day in a queue whose whole value is
            // that everything in it needs a decision.
            var result = await IngestAsync(
                app, Guid.NewGuid(), "RxSubmitted", RxMessage(app.Tenant) with { RequiresApproval = false });

            result.Outcome.Should().Be(RoutingOutcome.NotGated);
            result.AuthorizationId.Should().BeNull();

            await using var db = ApprovalsApiFactory.Ctx();
            (await db.Authorizations.AsNoTracking().CountAsync(a => a.TenantId == app.Tenant)).Should().Be(0);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_gated_prescription_does_raise_one()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            var result = await IngestAsync(app, Guid.NewGuid(), "RxSubmitted", RxMessage(app.Tenant));

            result.Outcome.Should().Be(RoutingOutcome.Raised);

            await using var db = ApprovalsApiFactory.Ctx();
            var auth = await db.Authorizations.AsNoTracking().SingleAsync(a => a.AuthorizationId == result.AuthorizationId);
            auth.Source.Should().Be(AuthSource.Prescription);
            auth.SourceRef.Should().Be(RxId.ToString());

            // No requesting provider, and that is not an omission: a doctor's token is practitioner-scoped and
            // carries no provider, so `Prescription` has no such column. The ingestion ENDPOINT refuses a
            // non-manual request without one — a rule aimed at an external caller — and applying it here
            // would dead-letter every gated prescription in the platform.
            auth.RequestingProviderId.Should().BeNull();
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_redelivered_routing_event_does_not_raise_a_second_request()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            // At-least-once delivery means this happens. The consumer's processed_event ledger is a read
            // followed by a write and it runs at prefetch 20, so the guard that actually holds under
            // concurrency is the PRIMARY KEY on processed_request, which is what this exercises.
            var eventId = Guid.NewGuid();
            var first = await IngestAsync(app, eventId, "OrderPendingApproval", OrderMessage(app.Tenant));
            var replay = await IngestAsync(app, eventId, "OrderPendingApproval", OrderMessage(app.Tenant));

            replay.Outcome.Should().Be(RoutingOutcome.Duplicate);
            replay.AuthorizationId.Should().Be(first.AuthorizationId);

            await using var db = ApprovalsApiFactory.Ctx();
            (await db.Authorizations.AsNoTracking().CountAsync(a => a.TenantId == app.Tenant)).Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_re_gated_amendment_raises_a_NEW_request_for_the_same_order()
    {
        Skip.If(ApprovalsApiFactory.Db is null, "APPROVALS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ApprovalsApiFactory();
        try
        {
            // Design 46 §5: an amendment that leaves the approved scope re-publishes OrderPendingApproval for
            // the SAME order, and that second request is a real one — the authorisation's basis no longer
            // holds. Deduping on (source, sourceRef) would swallow it and leave the order sitting in
            // PendingApproval with nothing in any queue, which is why the key is the event id.
            var first = await IngestAsync(app, Guid.NewGuid(), "OrderPendingApproval", OrderMessage(app.Tenant));
            var again = await IngestAsync(app, Guid.NewGuid(), "OrderPendingApproval", OrderMessage(app.Tenant));

            again.Outcome.Should().Be(RoutingOutcome.Raised);
            again.AuthorizationId.Should().NotBe(first.AuthorizationId!.Value);
        }
        finally { await app.CleanupAsync(); }
    }

    // ---------------------------------------------------------------- helpers

    private static readonly Guid OrderId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid RxId = Guid.Parse("00000000-0000-0000-0000-0000000000bb");

    private static async Task<RoutingResult> IngestAsync(
        ApprovalsApiFactory app, Guid eventId, string eventType, RoutingMessage msg)
    {
        app.CreateClient();   // realise the host
        using var scope = app.Services.CreateScope();
        // The consumer binds the RLS tenant from the envelope because it has no HTTP principal; so does this.
        scope.ServiceProvider.GetRequiredService<RlsContext>().TenantId = msg.TenantId!;
        return await scope.ServiceProvider.GetRequiredService<RoutedAuthorizationIngestor>()
            .IngestAsync(eventId, eventType, msg);
    }

    private static RoutingMessage OrderMessage(string? tenant) => new(
        tenant, Beneficiary, Encounter, Provider, OrderId, PrescriptionId: null,
        OrderNo: "ORD-2026-000900", RxNo: null, Reason: "high-cost-imaging",
        OrderedByUserId: "dr-hana", RequiresApproval: null, ServiceCodes: ["85025", "80053"]);

    private static RoutingMessage RxMessage(string? tenant) => new(
        tenant, Beneficiary, Encounter, ProviderId: null, OrderId: null, RxId,
        OrderNo: null, RxNo: "RX-2026-000410", Reason: null,
        OrderedByUserId: "dr-hana", RequiresApproval: true, ServiceCodes: ["d-augmentin-1g"]);
}
