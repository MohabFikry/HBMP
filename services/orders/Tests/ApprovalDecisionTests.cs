using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Data;
using Mersal.Events;
using Mersal.Orders.Api;
using Mersal.Orders.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Orders.Tests;

/// <summary>
/// The RETURN leg of the prior-authorization saga: a decision reaches the order that was waiting for it.
/// </summary>
/// <remarks>
/// <para><b>The gap these close.</b> <see cref="OrderWorkflow"/> has declared
/// <c>PendingApproval → Approved → Active</c> since phase 4 and nothing in the platform executed it — no
/// service consumed <c>approvals.events</c> at all. A gated order therefore sat in PendingApproval for ever
/// whatever a reviewer decided, and a REJECTED one was indistinguishable from one still in the queue: both
/// read "waiting" on every screen, so the only honest thing a desk could tell a patient was nothing.</para>
/// <para>The first test drives the whole thing: an Imaging order (gated by the shipped routing config) is
/// created through the real endpoint, and the decision that comes back leaves it Active — which is the state
/// the technician's queue reads. Everything after it is a case where the naive version would be wrong.</para>
/// </remarks>
[Collection("orders-db")]
public class ApprovalDecisionTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---------------------------------------------------------------- the mirror (no DB needed)

    [Fact]
    public void Orders_gets_its_own_copy_of_every_settling_decision()
    {
        // Point-to-point transport: one shared queue would have RabbitMQ deal each decision to orders OR
        // pharmacy, so half of each service's approvals would land on the other and be discarded.
        ApprovalDecisionFeed.OrdersQueue.Should().NotBe(ApprovalDecisionFeed.PharmacyQueue);
        ApprovalDecisionFeed.Queues.Should().Contain(ApprovalDecisionFeed.OrdersQueue);

        foreach (var settled in new[]
                 { "AuthApproved", "AuthPartiallyApproved", "AuthRejected", "AuthOverridden", "AuthEmergencyApproved" })
            ApprovalDecisionFeed.Includes(settled).Should().BeTrue($"{settled} settles a request");

        // A reviewer asking for more information is not an answer: the order stays PendingApproval, which it
        // already is, so a consumer would have nothing to do but risk moving something.
        ApprovalDecisionFeed.Includes("AuthInfoRequested").Should().BeFalse();
        ApprovalDecisionFeed.Includes("AuthSubmitted").Should().BeFalse();
    }

    // ---------------------------------------------------------------- the saga (DB)

    [SkippableFact]
    public async Task A_gated_order_is_routed_for_approval_and_the_approval_makes_it_actionable()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, _) = await SeedGatedOrderAsync(app);

            await using (var db = OrdersApiFactory.Ctx())
            {
                var routed = await db.Orders.AsNoTracking().SingleAsync(o => o.OrderId == orderId);
                routed.Status.Should().Be(OrderStatus.PendingApproval, "an Imaging order is gated by the shipped routing config");
            }

            var authorizationId = Guid.NewGuid();
            var result = await ApplyAsync(app, Decision(app, orderId, authorizationId, releases: true));

            result.Outcome.Should().Be(ApprovalApplyOutcome.Released);

            await using var after = OrdersApiFactory.Ctx();
            var order = await after.Orders.AsNoTracking().SingleAsync(o => o.OrderId == orderId);

            // ACTIVE, not Approved. 23 §2 lists approve and activate as two rows, but there is nothing anyone
            // can do with an Approved order and no second trigger to wait for — the technician's queue admits
            // Active / PartiallyUsed / Expired, so stopping at Approved would leave the patient in front of a
            // bench whose worklist is empty.
            order.Status.Should().Be(OrderStatus.Active);
            order.AuthorizationId.Should().Be(authorizationId, "the order records which decision released it");

            // Both transitions are on the wire, in the same transaction as the state change.
            Types(app).Should().Contain("OrderApproved").And.Contain("OrderActivated");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_rejection_settles_the_order_instead_of_leaving_it_waiting_for_ever()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, _) = await SeedGatedOrderAsync(app);

            var result = await ApplyAsync(app, Decision(app, orderId, Guid.NewGuid(), releases: false));

            result.Outcome.Should().Be(ApprovalApplyOutcome.Rejected);

            await using var db = OrdersApiFactory.Ctx();
            var order = await db.Orders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.OrderId == orderId);
            order.Status.Should().Be(OrderStatus.Rejected);

            // The LINES are untouched, deliberately. Rejected is terminal and the technician's queue admits
            // only Active / PartiallyUsed / Expired, so the order has already left every worklist. Cancelling
            // the lines as well would record a line-level withdrawal that nobody performed.
            order.Lines.Should().OnlyContain(l => l.Status == OrderLineStatus.Active);

            Types(app).Should().Contain("OrderRejected");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_partial_approval_narrows_the_order_rather_than_refusing_it()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, _) = await SeedGatedOrderAsync(app, twoLines: true);

            // The reviewer allowed one of the two codes. The other is cancelled, not the whole order: a
            // two-test order with one refusal is one test the patient should still have today.
            var result = await ApplyAsync(app,
                Decision(app, orderId, Guid.NewGuid(), releases: true, scope: [FirstCode]));

            result.Outcome.Should().Be(ApprovalApplyOutcome.Released);

            await using var db = OrdersApiFactory.Ctx();
            var order = await db.Orders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.OrderId == orderId);
            order.Status.Should().Be(OrderStatus.Active);

            var kept = order.Lines.Single(l => l.Code == FirstCode);
            var dropped = order.Lines.Single(l => l.Code == SecondCode);

            kept.Status.Should().Be(OrderLineStatus.Active);
            kept.QuantityOrdered.Should().Be(kept.RequestedQuantity, "an in-scope line keeps what was asked for");

            dropped.Status.Should().Be(OrderLineStatus.Cancelled);
            // WHY, WHO and WHEN — the database refuses a cancelled line without all three
            // (ck_order_line_amendment_attributed), and the actor is the REVIEWER whose decision it was, not
            // the background consumer that carried it.
            dropped.AmendmentReasonCode.Should().Be("not-in-approved-scope");
            dropped.AmendedBy.Should().Be(Reviewer);
            dropped.AmendedAt.Should().NotBeNull();
            // The QUANTITIES on the refused line are untouched, and that is not laziness. A partial approval
            // carries CODES and no quantities at all (DecisionRules.ValidatePartialScope), and orders 0013's
            // signed-content trigger refuses an in-place edit of quantity_ordered outright — so zeroing it
            // would fail the write, not merely overstate the decision. The status is the restriction.
            dropped.QuantityOrdered.Should().Be(dropped.RequestedQuantity);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_full_approval_carries_no_scope_and_narrows_nothing()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, _) = await SeedGatedOrderAsync(app, twoLines: true);

            // The dangerous reading of an absent scope is "nothing was approved", which would cancel every
            // line of a fully approved order on the strength of a missing field. approvals only ever sends a
            // scope for a PARTIAL approval, and validates it as a strict subset.
            await ApplyAsync(app, Decision(app, orderId, Guid.NewGuid(), releases: true, scope: null));

            await using var db = OrdersApiFactory.Ctx();
            var order = await db.Orders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.OrderId == orderId);
            order.Lines.Should().OnlyContain(l => l.Status == OrderLineStatus.Active);
            order.Lines.Should().OnlyContain(l => l.QuantityOrdered == l.RequestedQuantity);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_redelivered_decision_does_not_release_the_order_twice()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, _) = await SeedGatedOrderAsync(app);
            var msg = Decision(app, orderId, Guid.NewGuid(), releases: true);

            await ApplyAsync(app, msg);
            var replay = await ApplyAsync(app, msg);

            // The consumer's processed_event ledger catches a redelivered MESSAGE id. This is the guard that
            // survives a redelivery arriving under a NEW id, which the ledger has never seen: the workflow
            // table has no Active → Approved, so there is nothing to move and nothing to publish.
            replay.Outcome.Should().Be(ApprovalApplyOutcome.NotWaiting);
            Types(app).Count(t => t == "OrderActivated").Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_prescription_decision_is_ignored_rather_than_dead_lettered()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            // Both decision queues receive every decision and each filters by source — routing by payload at
            // the relay would put approvals' AuthSource vocabulary in the publisher. Filtering costs a
            // discarded message; mis-routing costs a decision that reaches nobody.
            var result = await ApplyAsync(app,
                Decision(app, Guid.NewGuid(), Guid.NewGuid(), releases: true) with { Source = "Prescription" });

            result.Outcome.Should().Be(ApprovalApplyOutcome.NotOurs);
        }
        finally { await app.CleanupAsync(); }
    }

    // ---------------------------------------------------------------- the producer's half of the contract

    [SkippableFact]
    public async Task The_routing_event_carries_everything_an_authorization_is_created_from()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            await SeedGatedOrderAsync(app);

            var routed = app.Outbox.AllMessages.Single(m => m.EventType == "OrderPendingApproval");
            using var doc = JsonDocument.Parse(routed.Payload);
            var p = doc.RootElement;

            // These field names are approvals' `RoutingMessage`. The consumer refuses a message without a
            // tenant or a beneficiary (dead-letter), and the database refuses an authorization attributable
            // to nobody — so an omission here does not degrade the feature, it stops it.
            p.GetProperty("tenantId").GetString().Should().Be(app.Tenant);
            p.GetProperty("orderId").GetGuid().Should().NotBeEmpty();
            p.GetProperty("beneficiaryId").GetGuid().Should().NotBeEmpty();
            p.GetProperty("encounterId").GetGuid().Should().NotBeEmpty();
            p.GetProperty("orderedByUserId").GetString().Should().NotBeNullOrWhiteSpace();
            p.GetProperty("orderNo").GetString().Should().StartWith("ORD-");

            // The requested codes: a partial approval must be a strict subset of them, so an authorization
            // ingested without them can only be approved or rejected outright — which is the decision the
            // approval team least often wants to make.
            p.GetProperty("serviceCodes").EnumerateArray().Should().NotBeEmpty();
        }
        finally { await app.CleanupAsync(); }
    }

    // ---------------------------------------------------------------- helpers

    private static readonly Guid Reviewer = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private const string FirstCode = "70450";
    private const string SecondCode = "71250";

    private static string[] Types(OrdersApiFactory app) =>
        app.Outbox.AllMessages.Select(m => m.EventType ?? "").ToArray();

    private static ApprovalDecisionMessage Decision(
        OrdersApiFactory app, Guid orderId, Guid authorizationId, bool releases, string[]? scope = null) =>
        new(app.Tenant, authorizationId, "AUTH-2026-000123", "OrderLine", orderId.ToString(), releases, scope,
            false, Reviewer);

    private static async Task<ApprovalApplyResult> ApplyAsync(OrdersApiFactory app, ApprovalDecisionMessage msg)
    {
        app.CreateClient();   // realise the host
        using var scope = app.Services.CreateScope();
        // The consumer binds the RLS tenant from the envelope because it has no HTTP principal; so does this.
        scope.ServiceProvider.GetRequiredService<RlsContext>().TenantId = msg.TenantId!;
        return await scope.ServiceProvider.GetRequiredService<OrderApprovalApplier>().ApplyAsync(msg, default);
    }

    /// <summary>An IMAGING order, which the shipped routing config gates — so this goes to PendingApproval
    /// through the real endpoint and the real policy, not by writing a status into a row.</summary>
    private static async Task<(Guid OrderId, Guid LineId)> SeedGatedOrderAsync(
        OrdersApiFactory app, bool twoLines = false)
    {
        using var doctor = app.DoctorClient();
        var body = new CreateOrderRequest(
            BeneficiaryId: Guid.NewGuid(), EncounterId: Guid.NewGuid(), OrderType: OrderType.Imaging, ExpiresAt: null,
            Lines: twoLines
                ? [new CreateOrderLine(CodeSystem.CPT, FirstCode, "CT head without contrast", 1m),
                   new CreateOrderLine(CodeSystem.CPT, SecondCode, "CT chest without contrast", 1m)]
                : [new CreateOrderLine(CodeSystem.CPT, FirstCode, "CT head without contrast", 1m)]);

        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/investigation-orders", UriKind.Relative))
        {
            Content = JsonContent.Create(body, options: Web),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var r = await doctor.SendAsync(req);
        r.StatusCode.Should().Be(HttpStatusCode.Created, "the seed itself must succeed or every assertion below is vacuous");

        var order = await r.Content.ReadFromJsonAsync<JsonElement>();
        return (order.GetProperty("orderId").GetGuid(), order.GetProperty("lines")[0].GetProperty("orderLineId").GetGuid());
    }
}
