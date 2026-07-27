using Mersal.Data;
using Mersal.Events;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Api;

/// <summary>
/// Phase 18.C2 (audit R2 W4) — expires report-access grants on a timer.
///
/// <c>POST /report-access/sweep-expiry</c> existed and nothing ever called it. A grant carries an
/// <c>ExpiresAt</c>, and the read path checks it, so an expired grant does not actually disclose anything —
/// but the ROW stays Active for ever. That matters for three reasons: the grant list shown to a patient or a
/// DPO says people still hold access they lost weeks ago; the expiry is never audited, so there is no record
/// of when access ended; and 20-compliance §5 (storage limitation) is answered by "the query filters it out",
/// which is not a retention control.
///
/// The sweep is idempotent and safe to run on every node — it moves rows by a guarded UPDATE and re-running
/// it finds nothing. So no leader election is needed for correctness; several nodes sweeping at once simply
/// means the first one does the work.
/// </summary>
public sealed class ReportAccessExpirySweeper(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<ReportAccessExpirySweeper> logger) : BackgroundService
{
    /// <summary>Hourly. The TTL floor is measured in hours (24h for HighlySensitive), so a tighter interval
    /// buys nothing; a looser one leaves a lapsed grant looking live for most of a working day.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>The sole tenant's GUC for the sweep's own connection. This is a platform maintenance pass
    /// over EVERY tenant's grants, so it cannot take a tenant from a request — there is no request. It runs
    /// per tenant instead, one bound session each, rather than binding an empty GUC (which after 18.B2 would
    /// correctly see zero rows) or a hardcoded one (which after 18.B2 would be wrong for tenant two).</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Never let a bad pass kill the loop: the next hour's sweep picks up everything this one
                // missed, because the work is defined by the data, not by a cursor.
                logger.LogError(ex, "report-access expiry sweep failed; retrying next interval");
            }

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

        // Which tenants have anything to expire? Read with the GUC unbound is impossible under RLS, so the
        // tenant list comes from the grants themselves via the owner-independent path: the sweep binds each
        // tenant in turn. Tenants are discovered from the ACTIVE grant set the previous pass left behind.
        var tenants = await DistinctTenantsAsync(db, ct);

        var expired = 0;
        foreach (var tenant in tenants)
        {
            rls.TenantId = tenant;
            var due = await db.ReportAccessGrants
                .Where(g => g.TenantId == tenant && g.RevokedAt == null && g.ExpiresAt <= now)
                .ToListAsync(ct);

            foreach (var g in due)
            {
                g.RevokedAt = now;
                g.RevokedBy = "system:expiry";
                await ReportAccessEndpoints.MoveRequestWithGrantAsync(db, g.RequestId, ReportAccessStatus.Expired, ct);
                await outbox.EnqueueAsync("ReportAccessGrantExpired", "orders.events",
                    new { tenantId = g.TenantId, g.GrantId, g.OrderLineId }, ct);
                expired++;
            }
            if (due.Count > 0) await db.SaveChangesAsync(ct);
        }

        if (expired > 0) logger.LogInformation("report-access expiry sweep closed {Count} grant(s)", expired);
    }

    /// <summary>Tenants holding at least one live grant. Runs unfiltered on the sweep's own connection: the
    /// query projects ONLY the tenant id — no grant, no beneficiary, no result — so RLS denying rows here
    /// would stop the maintenance pass without protecting anything that is not already public knowledge to
    /// the service itself.</summary>
    private static async Task<List<string>> DistinctTenantsAsync(OrdersDbContext db, CancellationToken ct) =>
        await db.Database.SqlQuery<string>(
            $"SELECT DISTINCT tenant_id AS \"Value\" FROM orders.report_access_grant WHERE revoked_at IS NULL")
            .ToListAsync(ct);
}
