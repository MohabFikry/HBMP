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

    [SkippableFact]
    public async Task Parallel_consume_of_one_line_lets_exactly_one_win()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
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

    [SkippableFact]
    public async Task Replaying_the_same_key_adds_no_row_and_returns_the_original()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
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

    [SkippableFact]
    public async Task Partial_then_remainder_moves_order_to_completed()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
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

    [SkippableFact]
    public async Task A_used_line_cannot_be_consumed_again()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
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

    [SkippableFact]
    public async Task Result_document_ref_and_value_pin_onto_the_consumed_fulfillment()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
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

    // ── 18.A3 / audit R2 X7 — the aggregate roll-up must not be a lost update ─────────────────────

    [SkippableFact]
    public async Task Parallel_consume_of_different_lines_completes_the_order()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            // Two lines, one unit each. Both racers succeed (different lines, no xmin collision), so the
            // order IS fully consumed. Before X7 both computed the aggregate from the graph they loaded
            // BEFORE the other committed, both wrote PartiallyUsed unguarded, and the order was stranded
            // there forever — OrderCompleted never emitted and the fulfilment saga never closed.
            //
            // The structural guarantee is the guarded compare-and-set in the executor, which converges
            // for EVERY interleaving. This test is the regression net: several rounds, because a run
            // that happens to serialize would also pass under the old code.
            for (var round = 0; round < 5; round++)
            {
                var (orderId, lineA, lineB) = await SeedActiveOrder(beneficiary, orderedQty: 1, extraLines: 1);

                var tasks = new[] { lineA, lineB }.Select(async (lineId, i) =>
                {
                    await using var ctx = Ctx();
                    return await new ConsumeExecutor(ctx).ConsumeAsync(
                        orderId, $"x7-{round}-{i}", Guid.NewGuid(), Guid.NewGuid(),
                        [new ConsumeLineRequest(lineId, 1)], DateTimeOffset.UtcNow);
                });
                var outcomes = await Task.WhenAll(tasks);

                outcomes.Should().OnlyContain(o => o.Outcome == ConsumeOutcome.Applied,
                    "the two racers touch DIFFERENT lines, so neither may lose");

                await using var verify = Ctx();
                (await verify.OrderLines.AsNoTracking().Where(l => l.OrderId == orderId).ToListAsync())
                    .Should().OnlyContain(l => l.Status == OrderLineStatus.Completed);
                (await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == orderId)).Status
                    .Should().Be(OrderStatus.Completed,
                        "round {0}: every line is consumed, so the order must not be stranded in PartiallyUsed", round);
            }
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task A_partially_consumed_order_still_settles_on_PartiallyUsed_under_concurrency()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            // Three lines, two consumed concurrently: the recompute must land on PartiallyUsed, not
            // over-shoot to Completed just because each racer saw only its own line.
            var (orderId, lineA, lineB) = await SeedActiveOrder(beneficiary, orderedQty: 1, extraLines: 2);

            var tasks = new[] { lineA, lineB }.Select(async (lineId, i) =>
            {
                await using var ctx = Ctx();
                return await new ConsumeExecutor(ctx).ConsumeAsync(
                    orderId, $"x7-partial-{i}", Guid.NewGuid(), Guid.NewGuid(),
                    [new ConsumeLineRequest(lineId, 1)], DateTimeOffset.UtcNow);
            });
            (await Task.WhenAll(tasks)).Should().OnlyContain(o => o.Outcome == ConsumeOutcome.Applied);

            await using var verify = Ctx();
            (await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == orderId)).Status
                .Should().Be(OrderStatus.PartiallyUsed);
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
