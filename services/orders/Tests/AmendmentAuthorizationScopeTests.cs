using FluentAssertions;
using Mersal.Amendment;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Tests;

/// <summary>
/// 30.4 — design 46 §5, proven against real rows, in BOTH directions.
///
/// <para>"Getting this backwards is costly in either direction: treat every amendment as re-approvable and
/// you flood the approval queue; treat none as re-approvable and you have built a way to obtain an approval
/// for one thing and dispense another." Each test below is one of those two failures.</para>
/// </summary>
[Collection("orders-db")]
public class AmendmentAuthorizationScopeTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("ORDERS_TEST_DB");
    private static OrdersDbContext Ctx() =>
        new(new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static readonly AmendReason Reason = new("ClinicalChange", "condition improved");

    [SkippableFact]
    public async Task An_IN_SCOPE_reduction_keeps_the_authorisation_and_troubles_nobody()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (orderId, lineId) = await Seed(beneficiary, quantity: 6, gated: true);

            AmendResult result;
            await using (var ctx = Ctx())
                result = await new AmendExecutor(ctx).AmendLineQuantityAsync(
                    orderId, lineId, "reduce", 4, Reason, Guid.NewGuid(), "Dr Karim", DateTimeOffset.UtcNow);

            result.Outcome.Should().Be(AmendOutcome.Applied);
            result.Impact.Should().Be(AuthorizationImpact.WithinApprovedScope);

            await using var verify = Ctx();
            (await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == orderId)).Status
                .Should().NotBe(OrderStatus.PendingApproval,
                    "reducing what was already approved needs no reviewer — sending it back floods the queue "
                    + "and teaches reviewers to rubber-stamp");
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task An_OUT_OF_SCOPE_increase_returns_the_order_to_pending_authorisation()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (orderId, lineId) = await Seed(beneficiary, quantity: 6, gated: true);

            AmendResult result;
            await using (var ctx = Ctx())
                result = await new AmendExecutor(ctx).AmendLineQuantityAsync(
                    orderId, lineId, "increase", 12, Reason, Guid.NewGuid(), "Dr Karim", DateTimeOffset.UtcNow);

            result.Outcome.Should().Be(AmendOutcome.Applied);
            result.Impact.Should().Be(AuthorizationImpact.BeyondApprovedScope);

            await using var verify = Ctx();
            (await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == orderId)).Status
                .Should().Be(OrderStatus.PendingApproval,
                    "the approval was a judgement about 6. Leaving it Active is a way to obtain an approval "
                    + "for one thing and have another performed");
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task An_UNGATED_order_is_never_sent_for_approval_however_far_it_is_amended()
    {
        // Most orders carry no authorisation. There is nothing to invalidate, and reporting these as
        // out-of-scope would put items in the queue no reviewer ever saw in the first place.
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (orderId, lineId) = await Seed(beneficiary, quantity: 6, gated: false);

            AmendResult result;
            await using (var ctx = Ctx())
                result = await new AmendExecutor(ctx).AmendLineQuantityAsync(
                    orderId, lineId, "big", 60, Reason, Guid.NewGuid(), "Dr Karim", DateTimeOffset.UtcNow);

            result.Impact.Should().Be(AuthorizationImpact.NotAuthorized);

            await using var verify = Ctx();
            (await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == orderId)).Status
                .Should().Be(OrderStatus.Active);
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task Cancelling_a_gated_line_never_sends_it_back_for_approval()
    {
        // Withdrawing something approved cannot exceed what was approved, and asking a reviewer to
        // re-approve nothing is exactly the queue-flooding failure.
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (orderId, lineId) = await Seed(beneficiary, quantity: 6, gated: true, extraLine: true);

            AmendResult result;
            await using (var ctx = Ctx())
                result = await new AmendExecutor(ctx).CancelLineAsync(
                    orderId, lineId, "cancel", Reason, Guid.NewGuid(), "Dr Karim", DateTimeOffset.UtcNow);

            result.Outcome.Should().Be(AmendOutcome.Applied);
            result.Impact.Should().Be(AuthorizationImpact.WithinApprovedScope);

            await using var verify = Ctx();
            (await verify.Orders.AsNoTracking().SingleAsync(o => o.OrderId == orderId)).Status
                .Should().NotBe(OrderStatus.PendingApproval);
        }
        finally { await Cleanup(beneficiary); }
    }

    private static async Task<(Guid orderId, Guid lineId)> Seed(
        Guid beneficiary, decimal quantity, bool gated, bool extraLine = false)
    {
        await using var ctx = Ctx();
        var line = new OrderLine
        {
            OrderLineId = Guid.NewGuid(), CodeSystem = CodeSystem.CPT, Code = "80053",
            QuantityOrdered = quantity, RequestedQuantity = quantity,
        };
        var lines = new List<OrderLine> { line };
        if (extraLine)
            lines.Add(new OrderLine
            {
                OrderLineId = Guid.NewGuid(), CodeSystem = CodeSystem.CPT, Code = "85025",
                QuantityOrdered = 1, RequestedQuantity = 1,
            });
        var order = new InvestigationOrder
        {
            OrderId = Guid.NewGuid(), OrderNo = await new OrderNoIssuer(ctx).NextAsync(2026),
            BeneficiaryId = beneficiary, EncounterId = Guid.NewGuid(), OrderingProviderId = Guid.NewGuid(),
            OrderType = OrderType.Lab, Status = OrderStatus.Active, RequestedAt = DateTimeOffset.UtcNow,
            // The presence of an authorization id IS "this was gated" — the same fact the routing decision
            // recorded when the order was placed.
            AuthorizationId = gated ? Guid.NewGuid() : null,
            Lines = lines,
        };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();
        return (order.OrderId, line.OrderLineId);
    }

    private static async Task Cleanup(Guid beneficiary)
    {
        await using var ctx = Ctx();
        var orderIds = await ctx.Orders.Where(o => o.BeneficiaryId == beneficiary)
            .Select(o => o.OrderId).ToListAsync();
        await ctx.LineAmendments.Where(a => orderIds.Contains(a.OrderId)).ExecuteDeleteAsync();
        await ctx.OrderLines.Where(l => orderIds.Contains(l.OrderId)).ExecuteDeleteAsync();
        await ctx.Orders.Where(o => o.BeneficiaryId == beneficiary).ExecuteDeleteAsync();
    }
}
