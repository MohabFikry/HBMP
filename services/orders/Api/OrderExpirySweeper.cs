using Mersal.Data;
using Mersal.Events;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Api;

/// <summary>
/// Moves lapsed investigation orders to <see cref="OrderStatus.Expired"/> on a timer.
/// </summary>
/// <remarks>
/// <para>
/// The twin of <c>PrescriptionExpirySweeper</c> in pharmacy-service, and here for the same reason: an order
/// that stopped being valid in March otherwise reads "Active" for ever in the row, in every report drawn
/// from it, and in the audit trail. "The fulfilment gate filters it out" is not a lifecycle.
/// </para>
/// <para>
/// Only <see cref="OrderStatus.Approved"/> and <see cref="OrderStatus.Active"/> and
/// <see cref="OrderStatus.PartiallyUsed"/> are swept. A <see cref="OrderStatus.PendingApproval"/> order past
/// its date is a REVIEW that has not happened, not a lapsed instruction — expiring it would quietly close a
/// decision the approval team still owes, and the queue would stop showing them what they are late on.
/// </para>
/// </remarks>
public sealed class OrderExpirySweeper(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<OrderExpirySweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "order expiry sweep failed; retrying next interval"); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<OrdersDbContext>();
        var rls = sp.GetRequiredService<RlsContext>();
        var outbox = sp.GetRequiredService<IOutbox>();
        var now = clock.GetUtcNow();

        var expired = 0;
        foreach (var tenant in await DistinctTenantsAsync(db, ct))
        {
            rls.TenantId = tenant;
            var due = await db.Orders
                .Where(o => o.TenantId == tenant
                            && (o.Status == OrderStatus.Approved || o.Status == OrderStatus.Active
                                || o.Status == OrderStatus.PartiallyUsed)
                            && o.ExpiresAt != null && o.ExpiresAt <= now)
                .ToListAsync(ct);

            if (due.Count == 0) continue;

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            foreach (var o in due)
            {
                o.Status = OrderStatus.Expired;
                await outbox.EnqueueAsync("OrderExpired", "orders.events",
                    new { tenantId = o.TenantId, orderId = o.OrderId, o.OrderNo, o.BeneficiaryId, orderType = o.OrderType.ToString(), o.ExpiresAt }, ct);
                expired++;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        if (expired > 0) logger.LogInformation("order expiry sweep closed {Count} order(s)", expired);
    }

    /// <summary>Tenants holding at least one still-actionable order. Projects ONLY the tenant id.</summary>
    private static async Task<List<string>> DistinctTenantsAsync(OrdersDbContext db, CancellationToken ct) =>
        await db.Database.SqlQuery<string>(
            $"SELECT DISTINCT tenant_id AS \"Value\" FROM orders.investigation_order WHERE status IN ('Approved','Active','PartiallyUsed')")
            .ToListAsync(ct);
}
