using Mersal.Events;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Provider.Api;

/// <summary>
/// 25.3 (design 42 §3) — warn before a licence lapses, and announce it on the day.
///
/// <para><b>Why a sweeper and not a query.</b> Nothing happens when a licence expires: no request is made, no
/// button is pressed, no event occurs. The date simply passes. Every other control on this platform hangs off
/// an action, so the only way a lapse becomes visible is if something goes looking — which is exactly the
/// shape <c>ReportAccessExpirySweeper</c> already has, and this follows it deliberately rather than inventing
/// a second pattern for the same problem.</para>
///
/// <para><b>90/60/30, then the day.</b> Following the existing <c>ProviderCredentialExpiring</c> precedent.
/// Ninety days is enough to start a renewal; thirty is enough to arrange cover; the day itself is when a
/// coordinator has to act on appointments that are already booked. The warnings go to the coordinators of
/// EVERY branch the practitioner serves, because a doctor working at Maadi and Dokki is one person whose
/// lapse is two clinics' problem.</para>
///
/// <para><b>Idempotent with no "last warned" column.</b> A threshold is matched on the EXACT day it is
/// crossed, so running the sweep twice in one day emits the same event id twice and the consumer dedupes;
/// running it the next day emits nothing until the next threshold. A `last_warned_at` column would have been
/// a second source of truth about a date already stored, and one that drifts the first time the sweep is
/// re-run after an outage.</para>
///
/// <para><b>It never cancels anything.</b> The expired event is a signal for emr to FLAG future appointments
/// and for a coordinator to decide who covers the clinic. A refugee's appointment is not cancelled by a
/// background service — see <c>PractitionerLicenceExpiredConsumer</c>.</para>
/// </summary>
public sealed class PractitionerLicenceExpirySweeper(
    IServiceScopeFactory scopeFactory,
    IBusinessCalendar calendar,
    ILogger<PractitionerLicenceExpirySweeper> logger) : BackgroundService
{
    /// <summary>Daily. The thresholds are whole days, so a tighter interval emits nothing new; the sweep is
    /// cheap enough that the interval is about noise, not cost.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>Delay before the first pass so a rolling deploy does not have every replica sweep at once
    /// during startup, when the outbox relay may not yet be draining.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Never let a bad pass kill the loop: tomorrow's sweep picks up everything this one missed,
                // because the work is defined by the data, not by a cursor.
                logger.LogError(ex, "practitioner licence expiry sweep failed; retrying next interval");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Exposed for the tests: one pass, over a stated date, so the thresholds can be asserted
    /// without waiting a day between them.</summary>
    public async Task<int> SweepAsync(CancellationToken ct, DateOnly? today = null)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<ProviderDbContext>();
        var outbox = sp.GetRequiredService<IOutbox>();
        var on = today ?? calendar.Today();   // 18.A3 — Africa/Cairo business date, never the wall clock

        // The widest threshold bounds the scan, and the expiry date itself is the lower bound: a licence that
        // lapsed last month has already been announced, and re-announcing it every day would bury the ones
        // that lapsed today under a backlog nobody can act on.
        var horizon = on.AddDays(PractitionerLicence.WarningDays.Max());
        var candidates = await db.Practitioners.AsNoTracking()
            .Include(p => p.BranchAssignments)
            .Where(p => !p.IsDeleted
                        && p.Status == PractitionerStatus.Active
                        && p.LicenseExpiry != null
                        && p.LicenseExpiry >= on
                        && p.LicenseExpiry <= horizon)
            .ToListAsync(ct);

        var emitted = 0;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        foreach (var p in candidates)
        {
            // The branches the warning is ABOUT. A practitioner with no active assignment is nobody's
            // rota problem yet, so there is no coordinator to tell.
            var branches = p.BranchAssignments
                .Where(a => a.Status == "Active" && a.ValidFrom <= on && (a.ValidTo == null || a.ValidTo >= on))
                .Select(a => a.BranchId).Distinct().ToList();
            if (branches.Count == 0) continue;

            var days = PractitionerLicence.DaysUntilExpiry(p.LicenseExpiry, on);

            if (days == 0)
            {
                // THE DAY. Two consumers, two purposes, and they must not share a queue: notification tells
                // the coordinators, emr flags the appointments. Same shape as PractitionerBranchRevoked,
                // which learned this the hard way — a publish to a queue with no matching consumer does not
                // fail, so both halves looked wired while one was never delivered.
                var payload = new
                {
                    tenantId = p.TenantId,
                    practitionerId = p.PractitionerId,
                    fullNameEn = p.FullNameEn,
                    fullNameAr = p.FullNameAr,
                    licenceExpiry = p.LicenseExpiry,
                    branchIds = branches,
                };
                await outbox.EnqueueAsync("PractitionerLicenceExpired", "provider.events", payload, ct);
                await outbox.EnqueueAsync("PractitionerLicenceExpired", "emr.practitioner-licence-expired", payload, ct);
                emitted++;
            }
            else if (PractitionerLicence.WarningThresholdCrossedOn(p.LicenseExpiry, on) is { } threshold)
            {
                await outbox.EnqueueAsync("PractitionerLicenceExpiring", "provider.events", new
                {
                    tenantId = p.TenantId,
                    practitionerId = p.PractitionerId,
                    fullNameEn = p.FullNameEn,
                    fullNameAr = p.FullNameAr,
                    licenceExpiry = p.LicenseExpiry,
                    daysRemaining = threshold,
                    branchIds = branches,
                }, ct);
                emitted++;
            }
        }

        if (emitted > 0) await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (emitted > 0)
            logger.LogInformation("practitioner licence sweep raised {Count} notice(s) on {Date}", emitted, on);
        return emitted;
    }
}
