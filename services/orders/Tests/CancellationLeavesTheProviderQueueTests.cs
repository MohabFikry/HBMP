using System.Net.Http.Json;
using FluentAssertions;
using Mersal.Orders.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Tests;

/// <summary>
/// 30.5 — design 46 §6: <b>"a notification is not propagation."</b> The failure mode is a cancelled order
/// that still sits in the lab's queue because only an email was sent, so the assertion has to be about the
/// QUEUE and not about the event.
///
/// <para>The Gate 0 audit found that these queues are LIVE QUERIES over <c>orders.order_line</c> rather than
/// read models fed by events, so the line leaves the queue in the SAME TRANSACTION as the cancellation —
/// invariant 6 is satisfied structurally rather than eventually, which is stronger than an event could make
/// it. These tests are what make that claim checkable rather than merely asserted in a comment.</para>
///
/// <para><b>What is asserted, and what is not.</b> The cancel goes through the real endpoint; the queue is
/// then read with <c>Queue.AvailableOrders</c>'s predicate, restated here, rather than through
/// <c>GET /investigation-orders/queue</c>. That endpoint returns <b>500</b> under this factory for a lab
/// principal — a pre-existing fault unrelated to amendment, recorded in docs/phase-30-gate-5-notes.md — and
/// asserting through a broken endpoint would prove nothing about cancellation while looking like it did. A
/// restated predicate is a copy, and copies drift, so the last test here reads the endpoint's source and
/// fails if it stops saying the same thing.</para>
/// </summary>
[Collection("orders-db")]
public class CancellationLeavesTheProviderQueueTests(OrdersApiFactory f) : IClassFixture<OrdersApiFactory>
{
    [SkippableFact]
    public async Task A_cancelled_line_is_gone_from_the_bench_queue_with_no_consumer_in_between()
    {
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var (orderId, lineId, otherLineId) = await SeedTwoLineActiveOrder();
            (await QueuedLineIds()).Should().Contain(lineId, "the line starts on the bench queue");

            var doctor = f.As(OrdersTestAuth.DoctorSub, "doctor", "orders:write orders:read");
            doctor.DefaultRequestHeaders.Add("Idempotency-Key", $"cancel-{Guid.NewGuid()}");
            var res = await doctor.PostAsJsonAsync(
                $"/api/v1/investigation-orders/{orderId}/lines/{lineId}/cancel",
                new { reasonCode = "ClinicalChange", reasonText = "no longer indicated" });
            res.IsSuccessStatusCode.Should().BeTrue();

            // NO WAIT, NO POLL, NO SLA. If this needed one, the queue would be a projection and the
            // assertion would have to be eventual — which is the shape design 46 §6 warns about.
            var after = await QueuedLineIds();
            after.Should().NotContain(lineId, "a withdrawn investigation must leave the bench immediately");
            after.Should().Contain(otherLineId, "and the rest of the order must stay on it");
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Cancelling_every_open_line_removes_the_order_from_the_queue_entirely()
    {
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var (orderId, _, _) = await SeedTwoLineActiveOrder();

            var doctor = f.As(OrdersTestAuth.DoctorSub, "doctor", "orders:write orders:read");
            doctor.DefaultRequestHeaders.Add("Idempotency-Key", $"bulk-{Guid.NewGuid()}");
            (await doctor.PostAsJsonAsync($"/api/v1/investigation-orders/{orderId}/cancel-lines",
                new { reasonCode = "Duplicate", reasonText = (string?)null })).IsSuccessStatusCode
                .Should().BeTrue();

            (await QueuedOrderIds()).Should().NotContain(orderId,
                "an order with nothing left to do must not sit on a worklist — a technician who opens it has "
                + "nothing to tell the patient in front of them");
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_AMENDED_line_leaves_the_queue_and_its_successor_takes_its_place()
    {
        // Amendment is not withdrawal. The superseded row must go — performing it would deliver the version
        // the doctor corrected — and the successor must appear, or the work silently stops being offered.
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var (orderId, lineId, _) = await SeedTwoLineActiveOrder(quantity: 4);

            var doctor = f.As(OrdersTestAuth.DoctorSub, "doctor", "orders:write orders:read");
            doctor.DefaultRequestHeaders.Add("Idempotency-Key", $"amend-{Guid.NewGuid()}");
            var res = await doctor.PostAsJsonAsync(
                $"/api/v1/investigation-orders/{orderId}/lines/{lineId}/amend",
                new { quantityOrdered = 2, reasonCode = "ClinicalChange", reasonText = (string?)null });
            res.IsSuccessStatusCode.Should().BeTrue();

            var queued = await QueuedLineIds();
            queued.Should().NotContain(lineId, "the superseded version must not be performed");

            await using var db = OrdersApiFactory.Ctx();
            var successor = await db.OrderLines.AsNoTracking()
                .SingleAsync(l => l.OrderId == orderId && l.VersionNo == 2);
            queued.Should().Contain(successor.OrderLineId, "the corrected version is the work now offered");
        }
        finally { await f.CleanupAsync(); }
    }

    [Fact]
    public void The_restated_filter_still_matches_the_endpoints()
    {
        // Guards the guard. The predicates below are a COPY of Queue.AvailableOrders, and a copy drifts — if
        // the endpoint's filter changed, these tests would keep passing while asserting something the queue
        // no longer does.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "services/orders/Api/Queue.cs"));

        source.Should().Contain(
            "o.Status == OrderStatus.Active || o.Status == OrderStatus.PartiallyUsed",
            "the queue's head-status filter changed; update the restated predicate in this file");
        source.Should().Contain(
            "l.Status == OrderLineStatus.Active || l.Status == OrderLineStatus.PartiallyUsed",
            "the queue's line-status filter changed; update the restated predicate in this file");
    }

    // ---- Queue.AvailableOrders, restated (see the class note) ------------------------------------------

    private async Task<List<Guid>> QueuedLineIds()
    {
        await using var db = OrdersApiFactory.Ctx();
        return await db.OrderLines.AsNoTracking()
            .Where(l => db.Orders.Any(o => o.OrderId == l.OrderId && o.TenantId == f.Tenant
                        && (o.Status == OrderStatus.Active || o.Status == OrderStatus.PartiallyUsed
                            || o.Status == OrderStatus.Expired)))
            .Where(l => l.Status == OrderLineStatus.Active || l.Status == OrderLineStatus.PartiallyUsed)
            .Select(l => l.OrderLineId).ToListAsync();
    }

    private async Task<List<Guid>> QueuedOrderIds()
    {
        await using var db = OrdersApiFactory.Ctx();
        return await db.Orders.AsNoTracking()
            .Where(o => o.TenantId == f.Tenant)
            .Where(o => o.Status == OrderStatus.Active || o.Status == OrderStatus.PartiallyUsed
                        || o.Status == OrderStatus.Expired)
            .Where(o => o.Lines.Any(l =>
                l.Status == OrderLineStatus.Active || l.Status == OrderLineStatus.PartiallyUsed))
            .Select(o => o.OrderId).ToListAsync();
    }

    private async Task<(Guid orderId, Guid lineId, Guid otherLineId)> SeedTwoLineActiveOrder(decimal quantity = 1)
    {
        await using var db = OrdersApiFactory.Ctx();
        var a = new OrderLine
        {
            OrderLineId = Guid.NewGuid(), TenantId = f.Tenant, CodeSystem = CodeSystem.CPT,
            Code = "80053", QuantityOrdered = quantity, RequestedQuantity = quantity,
        };
        var b = new OrderLine
        {
            OrderLineId = Guid.NewGuid(), TenantId = f.Tenant, CodeSystem = CodeSystem.CPT,
            Code = "85025", QuantityOrdered = 1, RequestedQuantity = 1,
        };
        var order = new InvestigationOrder
        {
            OrderId = Guid.NewGuid(), TenantId = f.Tenant,
            OrderNo = await new Infrastructure.OrderNoIssuer(db).NextAsync(2026),
            BeneficiaryId = Guid.NewGuid(), EncounterId = Guid.NewGuid(), OrderingProviderId = Guid.NewGuid(),
            OrderType = OrderType.Lab, Status = OrderStatus.Active, RequestedAt = DateTimeOffset.UtcNow,
            Lines = [a, b],
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return (order.OrderId, a.OrderLineId, b.OrderLineId);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
