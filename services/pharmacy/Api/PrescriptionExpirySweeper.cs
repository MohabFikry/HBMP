using Mersal.Data;
using Mersal.Events;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Api;

/// <summary>
/// Moves lapsed prescriptions to <see cref="RxStatus.Expired"/> on a timer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The read paths do not need this to be correct.</b> Dispensing has always compared <c>ExpiresAt</c>
/// against the clock, and the counter's view computes <c>Expired</c> the same way, so nothing is dispensable
/// in the window between lapsing and being swept. What the sweep changes is the ROW: without it a
/// prescription that stopped being valid in March still says "Approved" for ever, in the database, in every
/// report drawn from it, and in the audit trail. "The query filters it out" is not a lifecycle.
/// </para>
/// <para>
/// It also gives expiry a MOMENT. An RxExpired event is the only thing that can tell a notification service
/// to warn a patient holding an unfilled prescription, or a report that this tenant expires a third of what
/// it writes — neither of which can be derived from a status that silently stopped being true.
/// </para>
/// <para>
/// Idempotent and safe on every node: the work is defined by the data, so a second sweeper finds nothing.
/// Modelled on <c>ReportAccessExpirySweeper</c> in orders-service, including the per-tenant RLS binding —
/// this is a platform maintenance pass with no request behind it, so it cannot inherit a tenant from one.
/// </para>
/// </remarks>
public sealed class PrescriptionExpirySweeper(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<PrescriptionExpirySweeper> logger) : BackgroundService
{
    /// <summary>Hourly. Validity is measured in days, so a tighter interval buys nothing; much looser and a
    /// prescription that lapsed at midnight still reads "Approved" through a whole morning's dispensing.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Never let one bad pass kill the loop: the next hour picks up everything this one missed,
                // because the work is defined by the data and not by a cursor.
                logger.LogError(ex, "prescription expiry sweep failed; retrying next interval");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<PharmacyDbContext>();
        var rls = sp.GetRequiredService<RlsContext>();
        var outbox = sp.GetRequiredService<IOutbox>();
        var now = clock.GetUtcNow();

        var expired = 0;
        foreach (var tenant in await DistinctTenantsAsync(db, ct))
        {
            rls.TenantId = tenant;

            // Only statuses that were still ACTIONABLE. A Cancelled or fully-Dispensed prescription that
            // happens to be past its date is finished, not expired, and relabelling it would rewrite why it
            // stopped being dispensable.
            var due = await db.Prescriptions
                .Where(p => p.TenantId == tenant
                            && (p.Status == RxStatus.Approved || p.Status == RxStatus.PartiallyDispensed)
                            && p.ExpiresAt != null && p.ExpiresAt <= now)
                .ToListAsync(ct);

            if (due.Count == 0) continue;

            // 24.3 — enqueue and save share one transaction. An RxExpired announced for a prescription that
            // is still Approved cannot be un-sent.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            foreach (var rx in due)
            {
                rx.Status = RxStatus.Expired;
                await outbox.EnqueueAsync("RxExpired", "pharmacy.events",
                    new { tenantId = rx.TenantId, prescriptionId = rx.PrescriptionId, rx.RxNo, rx.BeneficiaryId, rx.ExpiresAt }, ct);
                expired++;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        if (expired > 0) logger.LogInformation("prescription expiry sweep closed {Count} prescription(s)", expired);
    }

    /// <summary>Tenants holding at least one still-actionable prescription. Projects ONLY the tenant id — no
    /// prescription, no beneficiary, no drug — so running it unfiltered on the sweep's own connection
    /// discloses nothing the service does not already know about itself.</summary>
    private static async Task<List<string>> DistinctTenantsAsync(PharmacyDbContext db, CancellationToken ct) =>
        await db.Database.SqlQuery<string>(
            $"SELECT DISTINCT tenant_id AS \"Value\" FROM pharmacy.prescription WHERE status IN ('Approved','PartiallyDispensed')")
            .ToListAsync(ct);
}
