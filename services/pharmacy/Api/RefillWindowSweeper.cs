using Mersal.Audit.Client;
using Mersal.Events;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Mersal.Prescribing;
using Microsoft.EntityFrameworkCore;
using Mersal.Time;

namespace Mersal.Pharmacy.Api;

/// <summary>
/// 29.5 — records the forfeiture of refill windows that closed uncollected (design 45 §5).
///
/// <para><b>This sweeper RECORDS; it does not ENFORCE.</b> A window past its close is already refused at the
/// counter, because <c>closes_at</c> is in <see cref="RefillWindows.MayDispense"/> rather than here. What
/// this adds is the RECORD — a status and a timestamp — because a forfeiture is money that will now never be
/// claimed, and "when did this become missed, and had the member's coverage already lapsed by then?" is a
/// question a benefit reconciliation asks.</para>
///
/// <para>That split is deliberate and is the whole design (see
/// docs/superpowers/specs/2026-08-07-chronic-refill-windows-design.md). If this job stalls, some closed
/// windows go unmarked for a while; no patient is refused who should have been served, and none is served
/// who should have been refused. Had the sweeper been responsible for promoting windows to Open, an outage
/// here would have turned into patients being turned away at a counter — a background job failing into a
/// clinical outage.</para>
///
/// <para>Modelled on <c>ReportAccessExpirySweeper</c>, including the per-tenant loop: this is a platform
/// maintenance pass over every tenant's windows, so it cannot take a tenant from a request — there is no
/// request.</para>
/// </summary>
public sealed class RefillWindowSweeper(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<RefillWindowSweeper> logger) : BackgroundService
{
    /// <summary>Hourly. Windows close on a DATE, so a tighter interval buys nothing; a looser one would leave
    /// a forfeited window looking collectable in the case team's view for most of a working day.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Never let a bad pass kill the loop. The next hour picks up everything this one missed,
                // because the work is defined by the DATA — windows past their close — and not by a cursor.
                logger.LogError(ex, "refill-window forfeiture sweep failed; retrying next interval");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PharmacyDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditClient>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();

        var now = clock.GetUtcNow();
        // Cairo, and derived from the SAME instant this sweep is stamped with.
        //
        // The error here ran the OTHER WAY to the one at the counter, and it is worth being exact about that
        // rather than filing both under "UTC is wrong". The UTC date LAGS Cairo, so a window that closed at
        // the end of yesterday was not swept until 02:00–03:00 the following morning: a couple of hours of
        // accidental grace, which costs a patient nothing. The counter's version of the same mistake refuses
        // someone their medicine. Fixed together because they are one defect, not because they weigh alike.
        var today = BusinessCalendar.DateIn(now);

        // The sweepable set, expressed the same way ShouldForfeit expresses it. Pending/Open only —
        // Blocked is excluded because the platform refused the patient, not the other way round, and
        // relabelling that as a no-show would destroy the only signal the case team has.
        var candidates = await db.DispenseWindows
            .Where(w => (w.Status == "Pending" || w.Status == "Open")
                        && w.DispensedQuantity == 0
                        && w.ClosesAt < today)
            .Take(500)
            .ToListAsync(ct);

        var forfeited = 0;
        foreach (var row in candidates)
        {
            // Re-asked through the DOMAIN rather than trusted from the query, so the rule has one definition.
            // The query is an index-friendly approximation of it; this is the rule.
            var domain = ToDomain(row);
            if (!RefillWindows.ShouldForfeit(domain, today)) continue;

            row.Status = nameof(WindowStatus.Missed);
            row.MissedAt = now;
            forfeited++;

            await outbox.EnqueueAsync("RefillWindowMissed", "pharmacy.events", new
            {
                tenantId = row.TenantId,
                prescriptionId = row.PrescriptionId,
                prescriptionLineId = row.PrescriptionLineId,
                windowNo = row.WindowNo,
                // The forfeited amount, so a benefit reconciliation can answer "how much did we schedule and
                // never hand over?". Zeroing it on the row would have destroyed exactly that.
                forfeitedQuantity = row.AllocatedQuantity,
                closedAt = row.ClosesAt,
                missedAt = now,
            }, ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "prescription_dispense_window", EntityId = row.WindowId.ToString(),
                Action = AuditAction.Update, ActorUserId = "system:refill-window-sweeper",
                TenantId = row.TenantId,
                DecisionOutcome = "Missed", DecisionReasonCode = "window-closed-undispensed",
            }, ct);
        }

        if (forfeited > 0)
        {
            // One transaction for the state changes AND their events — a crash between the two would either
            // lose the forfeiture notice or announce a forfeiture that never committed.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            logger.LogInformation("refill-window sweep forfeited {Count} window(s)", forfeited);
        }

        return forfeited;
    }

    private static RefillWindow ToDomain(PrescriptionDispenseWindow row) => new(
        WindowNo: row.WindowNo,
        ScheduledOpen: row.ScheduledOpenDate,
        OpensAt: row.OpensAt,
        ClosesAt: row.ClosesAt,
        AllocatedQuantity: row.AllocatedQuantity,
        DispensedQuantity: row.DispensedQuantity,
        Status: Enum.TryParse<WindowStatus>(row.Status, out var s) ? s : WindowStatus.Pending);
}
