using CsCheck;
using FluentAssertions;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Tests;

/// <summary>
/// Phase 18.F1 — property-based tests over <see cref="ConsumeExecutor"/> against REAL PostgreSQL.
///
/// The hand-written concurrency suite proves the invariants for the interleavings someone thought of:
/// 8 racers on one line, a replayed key, a subset consume. X7 was a lost update, and lost updates live in
/// the interleavings nobody thought of — a partial consume racing a full one, two different keys arriving
/// out of order, a replay landing between two distinct consumes.
///
/// This generates the SCENARIO rather than the assertion: a random ordered quantity, a random sequence of
/// (quantity, key) attempts including deliberate replays, executed concurrently, and then checks four
/// invariants that must hold no matter what happened in between:
///
///   1. 0 ≤ quantity_consumed ≤ quantity_ordered           — the accumulator never over- or under-flows
///   2. Σ fulfillment.quantity == line.quantity_consumed   — the ledger and the accumulator agree
///   3. aggregate status == RecomputeFrom(lines)           — the order's status is derived, not drifted
///   4. replaying a key adds no row and changes no total   — idempotency is a property, not a happy path
///
/// Invariant 2 is the one that would have caught X7 directly: a lost update moves the accumulator without a
/// matching fulfillment row (or the reverse), and no single-scenario test notices unless it happens to race
/// in exactly the right way.
///
/// Iterations are modest (each one is a real transaction against a real database) and each uses a unique
/// beneficiary id so runs are isolated and self-cleaning. Env-gated on ORDERS_TEST_DB like its siblings.
/// </summary>
[Collection("orders-db")]
public class ConsumeExecutorPropertyTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("ORDERS_TEST_DB");
    private static OrdersDbContext Ctx() =>
        new(new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    /// <summary>An attempt: how much to consume, and which key slot to use. Reusing a key slot IS a replay,
    /// which is how the generator produces idempotency races without special-casing them.</summary>
    private sealed record Attempt(int Quantity, int KeySlot);

    private static readonly Gen<(int Ordered, Attempt[] Attempts)> Scenario =
        Gen.Select(
            Gen.Int[1, 5],                                   // ordered quantity
            Gen.Int[1, 6].SelectMany(n =>                    // 1..6 attempts
                Gen.Select(Gen.Int[1, 4], Gen.Int[0, 2])     // quantity, key slot (0..2 ⇒ replays are likely)
                   .Select(t => new Attempt(t.Item1, t.Item2))
                   .Array[n]))
        .Select(t => (t.Item1, t.Item2));

    [SkippableFact]
    public async Task The_consume_invariants_hold_for_any_interleaving()
    {
        Skip.If(Db is null, "test DB not configured — set ORDERS_TEST_DB to run this DB integration test.");

        // 40 scenarios × up to 6 concurrent real transactions each. Enough to cover the interleaving space
        // that matters without turning the suite into a load test.
        foreach (var (ordered, attempts) in Draw(Scenario, 40))
        {
            var beneficiary = Guid.NewGuid();
            try
            {
                var (orderId, lineId) = await SeedOrder(beneficiary, ordered);

                // Fire every attempt CONCURRENTLY on its own connection — the interleaving is the point.
                await Task.WhenAll(attempts.Select(async a =>
                {
                    await using var ctx = Ctx();
                    await new ConsumeExecutor(ctx).ConsumeAsync(
                        orderId, $"prop-key-{a.KeySlot}", Guid.NewGuid(), Guid.NewGuid(),
                        [new ConsumeLineRequest(lineId, a.Quantity)], DateTimeOffset.UtcNow);
                }));

                await AssertInvariants(orderId, lineId, ordered, attempts);
            }
            finally { await Cleanup(beneficiary); }
        }
    }

    [SkippableFact]
    public async Task Replaying_a_key_is_always_a_no_op()
    {
        Skip.If(Db is null, "test DB not configured — set ORDERS_TEST_DB to run this DB integration test.");

        // Idempotency as a property: whatever the first call did, doing it again changes nothing. Stated
        // separately from the interleaving test because it must hold SEQUENTIALLY too — a replay minutes
        // later, from a retrying operator (18.D1), not just a concurrent duplicate.
        foreach (var (ordered, qty) in Draw(Gen.Select(Gen.Int[1, 5], Gen.Int[1, 5]), 15))
        {
            var beneficiary = Guid.NewGuid();
            try
            {
                var (orderId, lineId) = await SeedOrder(beneficiary, ordered);
                const string key = "replay-key";

                await using (var ctx = Ctx())
                    await new ConsumeExecutor(ctx).ConsumeAsync(orderId, key, Guid.NewGuid(), Guid.NewGuid(),
                        [new ConsumeLineRequest(lineId, qty)], DateTimeOffset.UtcNow);

                var (consumedAfterFirst, rowsAfterFirst) = await Totals(lineId);

                for (var i = 0; i < 3; i++)
                    await using (var ctx = Ctx())
                        await new ConsumeExecutor(ctx).ConsumeAsync(orderId, key, Guid.NewGuid(), Guid.NewGuid(),
                            [new ConsumeLineRequest(lineId, qty)], DateTimeOffset.UtcNow);

                var (consumedAfterReplays, rowsAfterReplays) = await Totals(lineId);
                consumedAfterReplays.Should().Be(consumedAfterFirst,
                    "a replayed key must not move the accumulator (ordered={0}, qty={1})", ordered, qty);
                rowsAfterReplays.Should().Be(rowsAfterFirst,
                    "a replayed key must not add a fulfillment row (ordered={0}, qty={1})", ordered, qty);
            }
            finally { await Cleanup(beneficiary); }
        }
    }


    /// <summary>
    /// Draw <paramref name="count"/> values from a generator.
    ///
    /// CsCheck's own <c>Sample</c> takes a synchronous assertion and runs it — which does not fit here,
    /// because each case opens real database connections and awaits concurrent transactions. So the cases
    /// are drawn up front and driven by the test's own async loop.
    ///
    /// The seed is FIXED. A property test that generates a different corpus on every run turns a real
    /// failure into "it was red yesterday" and makes a counter-example unreproducible — which for a
    /// concurrency invariant is the difference between a bug report and a shrug. Change the seed
    /// deliberately to widen the search; do not randomise it per run.
    /// </summary>
    private static List<T> Draw<T>(Gen<T> gen, int count, uint seed = 1801)
    {
        var pcg = new PCG(seed);
        var drawn = new List<T>(count);
        for (var i = 0; i < count; i++) drawn.Add(gen.Generate(pcg, null, out _));
        return drawn;
    }

    // ---- invariants ------------------------------------------------------------------------------------

    private static async Task AssertInvariants(Guid orderId, Guid lineId, int ordered, Attempt[] attempts)
    {
        await using var verify = Ctx();
        var line = await verify.OrderLines.AsNoTracking().SingleAsync(l => l.OrderLineId == lineId);
        var fulfillments = await verify.Fulfillments.AsNoTracking()
            .Where(f => f.OrderLineId == lineId).ToListAsync();
        var order = await verify.Orders.AsNoTracking().Include(o => o.Lines)
            .SingleAsync(o => o.OrderId == orderId);

        var scenario = $"ordered={ordered}, attempts=[{string.Join(", ", attempts.Select(a => $"{a.Quantity}@k{a.KeySlot}"))}]";

        // 1. The accumulator stays inside its bounds. X7 was a lost update — two concurrent consumes each
        //    reading the same starting value — which shows up here as consumed > ordered.
        line.QuantityConsumed.Should().BeGreaterThanOrEqualTo(0, "consumed cannot go negative ({0})", scenario);
        line.QuantityConsumed.Should().BeLessThanOrEqualTo(ordered,
            "consumed must never exceed ordered — this is the over-consume that X7 allowed ({0})", scenario);

        // 2. The immutable ledger and the mutable accumulator agree. A lost update breaks THIS even when it
        //    happens to leave the accumulator inside its bounds, which is why it is the sharper assertion.
        fulfillments.Sum(f => f.Quantity).Should().Be(line.QuantityConsumed,
            "Σ fulfillment.quantity must equal line.quantity_consumed ({0})", scenario);

        // 3. The order's status is DERIVED from its lines, never set independently. A status that drifts is
        //    how a fully-consumed order stays on a worklist, or an open one disappears from it.
        var expected = OrderConsume.RecomputeOrderStatus(order);
        order.Status.Should().Be(expected, "aggregate status must equal RecomputeFrom(lines) ({0})", scenario);

        // 4. One fulfillment row per DISTINCT key that succeeded — never one per attempt.
        var distinctKeys = attempts.Select(a => a.KeySlot).Distinct().Count();
        fulfillments.Count.Should().BeLessThanOrEqualTo(distinctKeys,
            "a replayed key must not produce a second fulfillment row ({0})", scenario);
    }

    private static async Task<(decimal Consumed, int Rows)> Totals(Guid lineId)
    {
        await using var ctx = Ctx();
        var line = await ctx.OrderLines.AsNoTracking().SingleAsync(l => l.OrderLineId == lineId);
        var rows = await ctx.Fulfillments.CountAsync(f => f.OrderLineId == lineId);
        return (line.QuantityConsumed, rows);
    }

    // ---- fixture ---------------------------------------------------------------------------------------

    private static async Task<(Guid OrderId, Guid LineId)> SeedOrder(Guid beneficiary, decimal ordered)
    {
        await using var ctx = Ctx();
        var line = new OrderLine
        {
            OrderLineId = Guid.NewGuid(), CodeSystem = CodeSystem.CPT, Code = "80053", QuantityOrdered = ordered,
        };
        var order = new InvestigationOrder
        {
            OrderId = Guid.NewGuid(), OrderNo = await new OrderNoIssuer(ctx).NextAsync(2026),
            BeneficiaryId = beneficiary, EncounterId = Guid.NewGuid(), OrderingProviderId = Guid.NewGuid(),
            OrderType = OrderType.Lab, Status = OrderStatus.Active, RequestedAt = DateTimeOffset.UtcNow,
            Lines = [line],
        };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();
        return (order.OrderId, line.OrderLineId);
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
