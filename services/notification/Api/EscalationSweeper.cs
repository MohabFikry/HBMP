using Mersal.Data;
using Mersal.Notification.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Notification.Api;

/// <summary>
/// Runs the escalation sweep. Audit §11.3 item 6.
///
/// <para><b>What was missing.</b> <see cref="EscalationService"/> was complete and correct: it finds actionable
/// notifications that are past their window, unread and not yet escalated, groups the channel rows of one event
/// so a recipient is escalated once rather than once per channel, and guards on <c>escalated_at</c> so it is
/// idempotent. It was registered in DI and constructed only by tests. Meanwhile
/// <see cref="NotificationDispatcher"/> stamps <c>EscalationDueAt</c> on every actionable notification it
/// creates. So the platform has been writing "escalate this at 14:32 if nobody acts" onto rows for three
/// phases, and 14:32 never arrived.</para>
///
/// <para>That is the failure mode worth naming: an escalation model that is configured, tested and inert reads
/// — to anyone looking at the routing table or the schema — as a working safety net. The three routes it
/// covers are an unanswered request for information on an authorization, a breached approval SLA and an
/// out-of-stock prescription line. All three are "somebody is waiting and nobody noticed".</para>
///
/// <para><b>Why a timer and not a scheduler.</b> The sweep is defined by the DATA, not by a cursor: it asks
/// what is due now. Re-running it finds nothing, so it is safe on every node and needs no leader election —
/// several nodes sweeping at once simply means the first one does the work. Same shape as
/// <c>ReportAccessExpirySweeper</c> in orders-service, deliberately.</para>
/// </summary>
public sealed class EscalationSweeper(
    IServiceScopeFactory scopeFactory,
    ILogger<EscalationSweeper> logger) : BackgroundService
{
    /// <summary>
    /// Every five minutes.
    ///
    /// The tightest window in the routing table is the SLA breach at two hours, so the interval only has to be
    /// small against that; five minutes puts the worst-case lateness at about 4% of the shortest window. A
    /// tighter loop would buy nothing an escalation can use — the recipient reads their inbox in minutes, not
    /// seconds — and would run an empty query all day on an installation with no actionable traffic.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Never let one bad pass kill the loop. The next pass picks up everything this one missed,
                // because what is due is a property of the rows rather than of where the last run got to.
                logger.LogError(ex, "escalation sweep failed; retrying next interval");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// One pass, per tenant.
    ///
    /// <para>There is no request, so there is no principal to take a tenant from — and an unbound session
    /// reads NOTHING after 18.F3 (the RLS interceptor binds a sentinel no row can equal). So the sweep binds
    /// each tenant in turn and runs the service once per tenant, rather than binding a blank GUC (which would
    /// correctly see zero rows and silently do nothing at all) or a hardcoded one (which would be wrong for
    /// every tenant but the first).</para>
    /// </summary>
    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<NotificationDbContext>();
        var rls = sp.GetRequiredService<RlsContext>();

        var total = 0;
        foreach (var tenant in await PendingTenantsAsync(db, ct))
        {
            rls.TenantId = tenant;
            total += await sp.GetRequiredService<EscalationService>().SweepAsync(ct);
        }

        if (total > 0) logger.LogInformation("escalation sweep raised {Count} escalation(s)", total);
    }

    /// <summary>
    /// Tenants holding at least one notification that could escalate.
    ///
    /// <para>Projects ONLY the tenant id — no subject, no body, no recipient — and applies the same predicate
    /// the sweep itself does, so a quiet installation runs one cheap query per interval instead of a full pass
    /// per tenant. RLS denying rows to this projection would stop the maintenance pass without protecting
    /// anything the service does not already hold.</para>
    /// </summary>
    private static async Task<List<string>> PendingTenantsAsync(NotificationDbContext db, CancellationToken ct) =>
        await db.Database.SqlQuery<string>(
            $"""
             SELECT DISTINCT tenant_id AS "Value" FROM notification.notification
             WHERE actionable AND read_at IS NULL AND escalated_at IS NULL
               AND escalation_due_at IS NOT NULL AND escalation_to_user_id IS NOT NULL
             """)
            .ToListAsync(ct);
}
