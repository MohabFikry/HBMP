using FluentAssertions;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Tests;

/// <summary>Phase 5.2 — the atomic-consume invariant proven against REAL parallel PostgreSQL transactions
/// (env-gated <c>ORDERS_TEST_DB</c>, not mocked): N racers on the SAME line yield EXACTLY ONE win and one
/// fulfillment row with quantity_consumed never exceeding ordered; replaying an Idempotency-Key adds no row;
/// a subset consume leaves the remainder Active/PartiallyUsed; a used line cannot be reused. Self-cleans by scope.</summary>
[Collection("orders-db")]
public class OrderConsumeConcurrencyTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("ORDERS_TEST_DB");
    private static DbContextOptions<OrdersDbContext> Options() =>
        new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;
    private static OrdersDbContext Ctx() => new(Options());

    [Fact]
    public async Task Parallel_consume_of_one_line_lets_exactly_one_win()
    {
        if (Db is null) return;
        var beneficiary = Guid.NewGuid();
        try
        {
            var (orderId, lineId, _) = await SeedActiveOrder(beneficiary, orderedQty: 1);

            const int racers = 8;
            var tasks = Enumerable.Range(0, racers).Select(async i =>
            {
                await using var ctx = Ctx();
                return await new ConsumeExecutor(ctx).ConsumeAsync(
                    orderId, $"key-{i}", Guid.NewGuid(), Guid.NewGuid(),
                    [new ConsumeLineRequest(lineId, 1)], DateTimeOffset.UtcNow);
            });
            var outcomes = await Task.WhenAll(tasks);

            outcomes.Count(o => o.Outcome == ConsumeOutcome.Applied).Should().Be(1, "exactly one racer may consume the line");
            // Every loser is a proper rejection — the exact kind depends on whether it lost the xmin race (Conflict)
            // or loaded after the winner committed (line already full/order closed). None may silently double-consume.
            outcomes.Where(o => o.Outcome != ConsumeOutcome.Applied).Should().OnlyContain(o =>
                o.Outcome == ConsumeOutcome.Conflict || o.Outcome == ConsumeOutcome.AlreadyUsed ||
                o.Outcome == ConsumeOutcome.OverConsume || o.Outcome == ConsumeOutcome.OrderNotConsumable);

            await using var verify = Ctx();
            var line = await verify.OrderLines.AsNoTracking().SingleAsync(l => l.OrderLineId == lineId);
            line.QuantityConsumed.Should().Be(1, "the accumulator must never exceed ordered");
            line.Status.Should().Be(OrderLineStatus.Completed);
            (await verify.Fulfillments.CountAsync(f => f.OrderLineId == lineId))
                .Should().Be(1, "exactly one immutable fulfillment row may exist");
            (await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == orderId)).Status
                .Should().Be(OrderStatus.Completed);
        }
        finally { await Cleanup(beneficiary); }
    }

    [Fact]
    public async Task Replaying_the_same_key_adds_no_row_and_returns_the_original()
    {
        if (Db is null) return;
        var beneficiary = Guid.NewGuid();
        try
        {
            var (orderId, lineId, _) = await SeedActiveOrder(beneficiary, orderedQty: 2);
            var provider = Guid.NewGuid();

            ConsumeResult first, replay;
            await using (var ctx = Ctx())
                first = await new ConsumeExecutor(ctx).ConsumeAsync(orderId, "same-key", provider, provider,
                    [new ConsumeLineRequest(lineId, 1)], DateTimeOffset.UtcNow);
            await using (var ctx = Ctx())
                replay = await new ConsumeExecutor(ctx).ConsumeAsync(orderId, "same-key", provider, provider,
                    [new ConsumeLineRequest(lineId, 1)], DateTimeOffset.UtcNow);

            first.Outcome.Should().Be(ConsumeOutcome.Applied);
            replay.Outcome.Should().Be(ConsumeOutcome.Replayed);
            replay.Fulfillments.Should().ContainSingle().Which.FulfillmentId
                .Should().Be(first.Fulfillments.Single().FulfillmentId, "replay returns the ORIGINAL fulfillment");

            await using var verify = Ctx();
            (await verify.Fulfillments.CountAsync(f => f.OrderLineId == lineId)).Should().Be(1);
            var line = await verify.OrderLines.AsNoTracking().SingleAsync(l => l.OrderLineId == lineId);
            line.QuantityConsumed.Should().Be(1, "the replay must not consume a second unit");
            line.Status.Should().Be(OrderLineStatus.PartiallyUsed);
        }
        finally { await Cleanup(beneficiary); }
    }

    [Fact]
    public async Task Partial_then_remainder_moves_order_to_completed()
    {
        if (Db is null) return;
        var beneficiary = Guid.NewGuid();
        try
        {
            var (orderId, lineId, _) = await SeedActiveOrder(beneficiary, orderedQty: 3);
            var provider = Guid.NewGuid();

            await using (var ctx = Ctx())
            {
                var r = await new ConsumeExecutor(ctx).ConsumeAsync(orderId, "p1", provider, provider,
                    [new ConsumeLineRequest(lineId, 1)], DateTimeOffset.UtcNow);
                r.Outcome.Should().Be(ConsumeOutcome.Applied);
                r.Order!.Status.Should().Be(OrderStatus.PartiallyUsed);
            }
            await using (var ctx = Ctx())
            {
                var r = await new ConsumeExecutor(ctx).ConsumeAsync(orderId, "p2", provider, provider,
                    [new ConsumeLineRequest(lineId, 2)], DateTimeOffset.UtcNow);
                r.Outcome.Should().Be(ConsumeOutcome.Applied);
                r.Order!.Status.Should().Be(OrderStatus.Completed);
            }

            await using var verify = Ctx();
            (await verify.Fulfillments.CountAsync(f => f.OrderLineId == lineId)).Should().Be(2);
            (await verify.OrderLines.AsNoTracking().SingleAsync(l => l.OrderLineId == lineId)).Status
                .Should().Be(OrderLineStatus.Completed);
        }
        finally { await Cleanup(beneficiary); }
    }

    [Fact]
    public async Task A_used_line_cannot_be_consumed_again()
    {
        if (Db is null) return;
        var beneficiary = Guid.NewGuid();
        try
        {
            // Two lines so the ORDER stays PartiallyUsed after line-1 completes — that isolates the line-level
            // no-reuse guard (AlreadyUsed) from the order-level "not consumable" guard.
            var (orderId, lineId, _) = await SeedActiveOrder(beneficiary, orderedQty: 1, extraLines: 1);
            var provider = Guid.NewGuid();

            await using (var ctx = Ctx())
                (await new ConsumeExecutor(ctx).ConsumeAsync(orderId, "use", provider, provider,
                    [new ConsumeLineRequest(lineId, 1)], DateTimeOffset.UtcNow)).Outcome.Should().Be(ConsumeOutcome.Applied);
            await using (var ctx = Ctx())
                (await new ConsumeExecutor(ctx).ConsumeAsync(orderId, "reuse", provider, provider,
                    [new ConsumeLineRequest(lineId, 1)], DateTimeOffset.UtcNow)).Outcome.Should().Be(ConsumeOutcome.AlreadyUsed);
        }
        finally { await Cleanup(beneficiary); }
    }

    [Fact]
    public async Task Result_document_ref_and_value_pin_onto_the_consumed_fulfillment()
    {
        if (Db is null) return;
        var beneficiary = Guid.NewGuid();
        try
        {
            var (orderId, lineId, _) = await SeedActiveOrder(beneficiary, orderedQty: 1);
            var provider = Guid.NewGuid();
            var documentId = Guid.NewGuid();

            Guid fulfillmentId;
            await using (var ctx = Ctx())
            {
                var r = await new ConsumeExecutor(ctx).ConsumeAsync(orderId, "res", provider, provider,
                    [new ConsumeLineRequest(lineId, 1)], DateTimeOffset.UtcNow);
                fulfillmentId = r.Fulfillments.Single().FulfillmentId;
            }
            // 5.3: a result may only attach to a CONSUMED line — the fulfillment row exists, so pin the blob ref.
            await using (var ctx = Ctx())
            {
                var f = await ctx.Fulfillments.SingleAsync(x => x.FulfillmentId == fulfillmentId);
                f.ResultValue = "WBC 6.1 x10^9/L";
                f.ResultDocumentId = documentId;
                f.ResultUploadedAt = DateTimeOffset.UtcNow;
                await ctx.SaveChangesAsync();
            }

            await using var verify = Ctx();
            var read = await verify.Fulfillments.AsNoTracking().SingleAsync(x => x.FulfillmentId == fulfillmentId);
            read.ResultDocumentId.Should().Be(documentId);
            read.ResultValue.Should().Be("WBC 6.1 x10^9/L");
            read.ResultUploadedAt.Should().NotBeNull();
        }
        finally { await Cleanup(beneficiary); }
    }

    private static async Task<(Guid orderId, Guid lineId, Guid otherLineId)> SeedActiveOrder(
        Guid beneficiary, decimal orderedQty, int extraLines = 0)
    {
        await using var ctx = Ctx();
        var line = new OrderLine { OrderLineId = Guid.NewGuid(), CodeSystem = CodeSystem.CPT, Code = "80053", QuantityOrdered = orderedQty };
        var lines = new List<OrderLine> { line };
        for (var i = 0; i < extraLines; i++)
            lines.Add(new OrderLine { OrderLineId = Guid.NewGuid(), CodeSystem = CodeSystem.CPT, Code = "85025", QuantityOrdered = 1 });
        var order = new InvestigationOrder
        {
            OrderId = Guid.NewGuid(), OrderNo = await new OrderNoIssuer(ctx).NextAsync(2026), BeneficiaryId = beneficiary,
            EncounterId = Guid.NewGuid(), OrderingProviderId = Guid.NewGuid(), OrderType = OrderType.Lab,
            Status = OrderStatus.Active, RequestedAt = DateTimeOffset.UtcNow, Lines = lines,
        };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();
        return (order.OrderId, line.OrderLineId, lines[^1].OrderLineId);
    }

    private static async Task Cleanup(Guid beneficiary)
    {
        await using var ctx = Ctx();
        var orderIds = await ctx.Orders.Where(o => o.BeneficiaryId == beneficiary).Select(o => o.OrderId).ToListAsync();
        var lineIds = await ctx.OrderLines.Where(l => orderIds.Contains(l.OrderId)).Select(l => l.OrderLineId).ToListAsync();
        await ctx.Fulfillments.Where(f => lineIds.Contains(f.OrderLineId)).ExecuteDeleteAsync();
        await ctx.OrderLines.Where(l => orderIds.Contains(l.OrderId)).ExecuteDeleteAsync();
        await ctx.Orders.Where(o => o.BeneficiaryId == beneficiary).ExecuteDeleteAsync();
    }
}
