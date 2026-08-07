using FluentAssertions;
using Mersal.Amendment;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Tests;

/// <summary>
/// 30.2 — design 46 §2: <b>"not yet consumed" is not a state you can read and then act on.</b>
///
/// <para>Between the doctor's click and the server's write, a technician may have begun. Checking first and
/// writing second is exactly the lost update the consume path already defends against — on the same rows,
/// with the same racers. So cancellation is a guarded transition and is proven the same way the consume
/// invariant is: against REAL parallel PostgreSQL transactions, not a mock.</para>
///
/// <para>Deliberately in the same collection and built on the same seed/cleanup helpers as
/// <see cref="OrderConsumeConcurrencyTests"/>. Phase-30 Gate 2: "Reuse the existing harness — do not write a
/// new one." A second harness would drift from the first, and the drift would be in the setup, which is
/// where a concurrency proof quietly stops proving anything.</para>
/// </summary>
[Collection("orders-db")]
public class CancelVsConsumeRaceTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("ORDERS_TEST_DB");
    private static OrdersDbContext Ctx() =>
        new(new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static readonly AmendReason Reason = new("ClinicalChange", "patient improved");

    [SkippableFact]
    public async Task Parallel_cancels_of_one_line_let_exactly_one_win()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (orderId, lineId, _) = await Seed(beneficiary, orderedQty: 2);

            const int racers = 8;
            var outcomes = await Task.WhenAll(Enumerable.Range(0, racers).Select(async i =>
            {
                await using var ctx = Ctx();
                return await new AmendExecutor(ctx).CancelLineAsync(
                    orderId, lineId, $"cancel-{i}", Reason, Guid.NewGuid(), "Dr Karim", DateTimeOffset.UtcNow);
            }));

            outcomes.Count(o => o.Outcome == AmendOutcome.Applied)
                .Should().Be(1, "a line is cancelled once, however many doctors press the button");
            outcomes.Where(o => o.Outcome != AmendOutcome.Applied).Should().OnlyContain(
                o => o.Outcome == AmendOutcome.Conflict || o.Outcome == AmendOutcome.AlreadyTerminal);

            await using var verify = Ctx();
            var line = await verify.OrderLines.AsNoTracking().SingleAsync(l => l.OrderLineId == lineId);
            line.Status.Should().Be(OrderLineStatus.Cancelled);
            line.AmendmentReasonCode.Should().Be("ClinicalChange");
            (await verify.LineAmendments.CountAsync(a => a.OrderLineId == lineId))
                .Should().Be(1, "exactly one immutable amendment record may exist");
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task A_cancel_racing_a_consume_produces_exactly_one_winner()
    {
        // THE acceptance criterion (design 46 §10). Both operations are legal on their own; only one may land.
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (orderId, lineId, _) = await Seed(beneficiary, orderedQty: 1);

            var cancels = Enumerable.Range(0, 4).Select(async i =>
            {
                await using var ctx = Ctx();
                var r = await new AmendExecutor(ctx).CancelLineAsync(
                    orderId, lineId, $"c-{i}", Reason, Guid.NewGuid(), "Dr Karim", DateTimeOffset.UtcNow);
                return (cancelled: r.Outcome == AmendOutcome.Applied, consumed: false);
            });
            var consumes = Enumerable.Range(0, 4).Select(async i =>
            {
                await using var ctx = Ctx();
                var r = await new ConsumeExecutor(ctx).ConsumeAsync(
                    orderId, $"u-{i}", Guid.NewGuid(), Guid.NewGuid(),
                    [new ConsumeLineRequest(lineId, 1)], DateTimeOffset.UtcNow);
                return (cancelled: false, consumed: r.Outcome == ConsumeOutcome.Applied);
            });

            var results = await Task.WhenAll(cancels.Concat(consumes));

            var winners = results.Count(r => r.cancelled || r.consumed);
            winners.Should().Be(1,
                "the line was either withdrawn or delivered — never both, and never neither");

            await using var verify = Ctx();
            var line = await verify.OrderLines.AsNoTracking().SingleAsync(l => l.OrderLineId == lineId);
            var fulfilments = await verify.Fulfillments.CountAsync(f => f.OrderLineId == lineId);
            var amendments = await verify.LineAmendments.CountAsync(a => a.OrderLineId == lineId);

            // The two consistent end states, and nothing between them. A cancelled line with a fulfilment row
            // would mean a withdrawn investigation was performed and billed.
            if (results.Any(r => r.consumed))
            {
                line.Status.Should().Be(OrderLineStatus.Completed);
                line.QuantityConsumed.Should().Be(1);
                fulfilments.Should().Be(1);
                amendments.Should().Be(0);
            }
            else
            {
                line.Status.Should().Be(OrderLineStatus.Cancelled);
                line.QuantityConsumed.Should().Be(0);
                fulfilments.Should().Be(0);
                amendments.Should().Be(1);
            }
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task The_loser_is_told_exactly_what_happened_and_who_did_it()
    {
        // Design 46 §2: "A doctor who is told 'someone else changed this' and nothing else will simply
        // retry" — and a retry after a dispense is how a cancelled-then-dispensed drug happens.
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (orderId, lineId, _) = await Seed(beneficiary, orderedQty: 1);
            var performedAt = DateTimeOffset.Parse("2026-08-07T14:32:00+00:00");
            var provider = Guid.NewGuid();

            await using (var ctx = Ctx())
                await new ConsumeExecutor(ctx).ConsumeAsync(orderId, "won", provider, Guid.NewGuid(),
                    [new ConsumeLineRequest(lineId, 1)], performedAt);

            AmendResult late;
            await using (var ctx = Ctx())
                late = await new AmendExecutor(ctx).CancelLineAsync(
                    orderId, lineId, "too-late", Reason, Guid.NewGuid(), "Dr Karim", DateTimeOffset.UtcNow);

            late.Outcome.Should().Be(AmendOutcome.AlreadyTerminal);
            late.Conflict.Should().NotBeNull("a bare 409 tells the doctor nothing they can act on");
            late.Conflict!.What.Should().Be("Consumed");
            late.Conflict.When.Should().BeCloseTo(performedAt, TimeSpan.FromSeconds(1),
                "the doctor needs the TIME — it is how they work out what happened");
            late.Conflict.PerformedByProviderId.Should().Be(provider);
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task A_replayed_cancel_applies_once_and_returns_the_original()
    {
        // A double-tapped cancel must not write two amendment records: the count of "how often do we cancel"
        // is a clinical-quality metric, and one nervous double-click would inflate it.
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (orderId, lineId, _) = await Seed(beneficiary, orderedQty: 2);
            var actor = Guid.NewGuid();

            AmendResult first, replay;
            await using (var ctx = Ctx())
                first = await new AmendExecutor(ctx).CancelLineAsync(
                    orderId, lineId, "same-key", Reason, actor, "Dr Karim", DateTimeOffset.UtcNow);
            await using (var ctx = Ctx())
                replay = await new AmendExecutor(ctx).CancelLineAsync(
                    orderId, lineId, "same-key", Reason, actor, "Dr Karim", DateTimeOffset.UtcNow);

            first.Outcome.Should().Be(AmendOutcome.Applied);
            replay.Outcome.Should().Be(AmendOutcome.Replayed);
            replay.AmendmentId.Should().Be(first.AmendmentId, "replay returns the ORIGINAL record");

            await using var verify = Ctx();
            (await verify.LineAmendments.CountAsync(a => a.OrderLineId == lineId)).Should().Be(1);
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task A_key_reused_for_a_DIFFERENT_reason_is_rejected_rather_than_answered_with_the_first()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (orderId, lineId, other) = await Seed(beneficiary, orderedQty: 2, extraLines: 1);
            var actor = Guid.NewGuid();

            await using (var ctx = Ctx())
                await new AmendExecutor(ctx).CancelLineAsync(
                    orderId, lineId, "shared", Reason, actor, "Dr Karim", DateTimeOffset.UtcNow);

            await using var ctx2 = Ctx();
            var second = await new AmendExecutor(ctx2).CancelLineAsync(
                orderId, other, "shared", new AmendReason("Duplicate", null), actor, "Dr Karim",
                DateTimeOffset.UtcNow);

            second.Outcome.Should().Be(AmendOutcome.IdempotencyKeyReuse,
                "answering with the first cancellation would tell the doctor a DIFFERENT line had been "
                + "withdrawn than the one they asked about");
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task Amend_supersedes_without_ever_mutating_the_original()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (orderId, lineId, _) = await Seed(beneficiary, orderedQty: 6);

            // Four of six delivered, then the course is shortened to five.
            var provider = Guid.NewGuid();
            for (var i = 0; i < 4; i++)
                await using (var ctx = Ctx())
                    await new ConsumeExecutor(ctx).ConsumeAsync(orderId, $"s-{i}", provider, provider,
                        [new ConsumeLineRequest(lineId, 1)], DateTimeOffset.UtcNow);

            AmendResult amended;
            await using (var ctx = Ctx())
                amended = await new AmendExecutor(ctx).AmendLineQuantityAsync(
                    orderId, lineId, "amend-1", newQuantity: 5, Reason, Guid.NewGuid(), "Dr Karim",
                    DateTimeOffset.UtcNow);

            amended.Outcome.Should().Be(AmendOutcome.Applied);
            amended.NewLineId.Should().NotBeNull();

            await using var verify = Ctx();
            var original = await verify.OrderLines.AsNoTracking().SingleAsync(l => l.OrderLineId == lineId);
            var successor = await verify.OrderLines.AsNoTracking().SingleAsync(l => l.OrderLineId == amended.NewLineId);

            original.Status.Should().Be(OrderLineStatus.Superseded);
            original.SupersededById.Should().Be(successor.OrderLineId);
            original.QuantityOrdered.Should().Be(6, "the ORIGINAL is never rewritten — it says what was ordered");
            original.QuantityConsumed.Should().Be(4);

            successor.VersionNo.Should().Be(2);
            successor.SupersedesId.Should().Be(lineId);
            successor.RootLineId.Should().Be(original.RootLineId, "both versions share one chain");
            successor.QuantityOrdered.Should().Be(5);
            successor.QuantityConsumed.Should().Be(4,
                "THE CONSUMED PORTION IS IMMUTABLE — the accumulator carries forward, so the beneficiary "
                + "gets one more session, not five more");
            successor.Status.Should().Be(OrderLineStatus.PartiallyUsed);
            successor.Code.Should().Be(original.Code);
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task An_amendment_below_what_was_already_consumed_is_refused()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (orderId, lineId, _) = await Seed(beneficiary, orderedQty: 6);
            var provider = Guid.NewGuid();
            for (var i = 0; i < 4; i++)
                await using (var ctx = Ctx())
                    await new ConsumeExecutor(ctx).ConsumeAsync(orderId, $"s-{i}", provider, provider,
                        [new ConsumeLineRequest(lineId, 1)], DateTimeOffset.UtcNow);

            await using var ctx2 = Ctx();
            var result = await new AmendExecutor(ctx2).AmendLineQuantityAsync(
                orderId, lineId, "too-low", newQuantity: 3, Reason, Guid.NewGuid(), "Dr Karim",
                DateTimeOffset.UtcNow);

            result.Outcome.Should().Be(AmendOutcome.BelowConsumed, "it implies un-delivering four sessions");

            await using var verify = Ctx();
            (await verify.OrderLines.AsNoTracking().CountAsync(l => l.RootLineId == lineId))
                .Should().Be(1, "a refused amendment creates no successor row");
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task An_unknown_reason_code_is_refused_before_anything_is_written()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (orderId, lineId, _) = await Seed(beneficiary, orderedQty: 2);

            await using var ctx = Ctx();
            var result = await new AmendExecutor(ctx).CancelLineAsync(
                orderId, lineId, "bad-reason", new AmendReason("BecauseISaidSo", null),
                Guid.NewGuid(), "Dr Karim", DateTimeOffset.UtcNow);

            result.Outcome.Should().Be(AmendOutcome.InvalidReason,
                "a reason column that accepts anything is a free-text column with extra steps, and every "
                + "report built on it is quietly wrong");
        }
        finally { await Cleanup(beneficiary); }
    }

    // ---------------------------------------------------------------- harness (shared with the consume suite)

    private static async Task<(Guid orderId, Guid lineId, Guid otherLineId)> Seed(
        Guid beneficiary, decimal orderedQty, int extraLines = 0)
    {
        await using var ctx = Ctx();
        var line = new OrderLine
        {
            OrderLineId = Guid.NewGuid(), CodeSystem = CodeSystem.CPT, Code = "80053", QuantityOrdered = orderedQty,
        };
        var lines = new List<OrderLine> { line };
        for (var i = 0; i < extraLines; i++)
            lines.Add(new OrderLine
            {
                OrderLineId = Guid.NewGuid(), CodeSystem = CodeSystem.CPT, Code = "85025", QuantityOrdered = 1,
            });
        var order = new InvestigationOrder
        {
            OrderId = Guid.NewGuid(), OrderNo = await new OrderNoIssuer(ctx).NextAsync(2026),
            BeneficiaryId = beneficiary, EncounterId = Guid.NewGuid(), OrderingProviderId = Guid.NewGuid(),
            OrderType = OrderType.Lab, Status = OrderStatus.Active, RequestedAt = DateTimeOffset.UtcNow,
            Lines = lines,
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
        await ctx.LineAmendments.Where(a => orderIds.Contains(a.OrderId)).ExecuteDeleteAsync();
        await ctx.Fulfillments.Where(f => lineIds.Contains(f.OrderLineId)).ExecuteDeleteAsync();
        // ONE statement for every version. A superseded line and its successor reference each other
        // (superseded_by_id / supersedes_id), so deleting either alone violates the other's FK; Postgres
        // checks referential integrity at end of statement, by which point both rows are gone.
        await ctx.OrderLines.Where(l => orderIds.Contains(l.OrderId)).ExecuteDeleteAsync();
        await ctx.Orders.Where(o => o.BeneficiaryId == beneficiary).ExecuteDeleteAsync();
    }
}
