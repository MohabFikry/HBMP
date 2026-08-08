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
/// <para><b>Asserted through the real endpoint.</b> An earlier version of this file read
/// <c>Queue.AvailableOrders</c>' predicate restated in the test, because <c>GET /queue</c> answered 500. That
/// turned out to be a genuine defect in the endpoint — non-nullable <c>page</c>/<c>pageSize</c>, so the
/// natural call with no query string never reached the handler — and a fixture whose lab client held less
/// scope than a real <c>lab_tech</c> token. Both are fixed, so these assertions now go over HTTP, which is
/// what the acceptance criterion asks for.</para>
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

    [SkippableFact]
    public async Task The_bench_queue_answers_a_call_with_no_query_string()
    {
        // THE REGRESSION. `GET /queue` took non-nullable page/pageSize, so the natural call — the one the
        // bench screen makes — died in the model binder with a 500 before the handler ran. The Page() helper
        // had always clamped and defaulted them; nothing ever let it. Unreachable to every existing test,
        // because the fixture's lab client also lacked the provider:read a real lab_tech token carries, so
        // the defect survived from phase 5 to phase 30.
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var res = await f.LabClient(Guid.NewGuid()).GetAsync("/api/v1/investigation-orders/queue");
        res.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    // ---- the queue, over HTTP -------------------------------------------------------------------------

    /// <summary>The bench queue AS A TECHNICIAN SEES IT — over HTTP, with no query string, which is the call
    /// the screen makes and the one that used to 500.</summary>
    private async Task<System.Text.Json.JsonElement> QueueAsync()
    {
        var res = await f.LabClient(Guid.NewGuid()).GetAsync("/api/v1/investigation-orders/queue");
        res.IsSuccessStatusCode.Should().BeTrue(
            $"a lab technician must be able to read their bench queue (got {res.StatusCode})");
        return await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
    }

    private async Task<List<Guid>> QueuedLineIds()
    {
        var ids = new List<Guid>();
        foreach (var item in (await QueueAsync()).EnumerateArray())
            foreach (var line in item.GetProperty("lines").EnumerateArray())
                ids.Add(line.GetProperty("orderLineId").GetGuid());
        return ids;
    }

    private async Task<List<Guid>> QueuedOrderIds() =>
        [.. (await QueueAsync()).EnumerateArray().Select(i => i.GetProperty("orderId").GetGuid())];

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
}
