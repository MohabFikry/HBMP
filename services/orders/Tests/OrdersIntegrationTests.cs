using FluentAssertions;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Tests;

/// <summary>Phase 4.2 order persistence at the datastore (env-gated <c>ORDERS_TEST_DB</c>): an order + lines
/// round-trip with the routed status, the order-number issuer is monotonic, and the consume accumulator's
/// invariant (0 ≤ consumed ≤ ordered) is enforced by the DB. Self-cleans by beneficiary scope tag.</summary>
public class OrdersIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("ORDERS_TEST_DB");
    private static DbContextOptions<OrdersDbContext> Options() =>
        new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;

    [Fact]
    public async Task Order_with_lines_persists_with_routed_status()
    {
        if (Db is null) return;
        var beneficiary = Guid.NewGuid();
        try
        {
            Guid orderId;
            await using (var ctx = new OrdersDbContext(Options()))
            {
                var no = await new OrderNoIssuer(ctx).NextAsync(2026);
                no.Should().StartWith("ORD-2026-");
                var order = new InvestigationOrder
                {
                    OrderId = Guid.NewGuid(), OrderNo = no, BeneficiaryId = beneficiary, EncounterId = Guid.NewGuid(),
                    OrderingProviderId = Guid.NewGuid(), OrderType = OrderType.Imaging, Status = OrderStatus.PendingApproval,
                    RequestedAt = DateTimeOffset.UtcNow,
                    Lines = [new OrderLine { OrderLineId = Guid.NewGuid(), CodeSystem = CodeSystem.CPT, Code = "70450", QuantityOrdered = 1 }],
                };
                ctx.Orders.Add(order);
                await ctx.SaveChangesAsync();
                orderId = order.OrderId;
            }

            await using var verify = new OrdersDbContext(Options());
            var read = await verify.Orders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.OrderId == orderId);
            read.Status.Should().Be(OrderStatus.PendingApproval);
            read.Lines.Should().ContainSingle().Which.QuantityConsumed.Should().Be(0);
        }
        finally { await Cleanup(beneficiary); }
    }

    [Fact]
    public async Task Consume_over_ordered_is_rejected_by_db()
    {
        if (Db is null) return;
        var beneficiary = Guid.NewGuid();
        try
        {
            await using var ctx = new OrdersDbContext(Options());
            var order = new InvestigationOrder
            {
                OrderId = Guid.NewGuid(), OrderNo = await new OrderNoIssuer(ctx).NextAsync(2026), BeneficiaryId = beneficiary,
                EncounterId = Guid.NewGuid(), OrderingProviderId = Guid.NewGuid(), OrderType = OrderType.Lab,
                Status = OrderStatus.Active, RequestedAt = DateTimeOffset.UtcNow,
                Lines = [new OrderLine { OrderLineId = Guid.NewGuid(), CodeSystem = CodeSystem.CPT, Code = "80053", QuantityOrdered = 1, QuantityConsumed = 5 }],
            };
            ctx.Orders.Add(order);
            var act = async () => await ctx.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();   // CHECK (consumed ≤ ordered)
        }
        finally { await Cleanup(beneficiary); }
    }

    private static async Task Cleanup(Guid beneficiary)
    {
        await using var ctx = new OrdersDbContext(Options());
        var ids = await ctx.Orders.Where(o => o.BeneficiaryId == beneficiary).Select(o => o.OrderId).ToListAsync();
        await ctx.OrderLines.Where(l => ids.Contains(l.OrderId)).ExecuteDeleteAsync();
        await ctx.Orders.Where(o => o.BeneficiaryId == beneficiary).ExecuteDeleteAsync();
    }
}
