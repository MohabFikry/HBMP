using Mersal.CallCentre.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.CallCentre.Infrastructure;

/// <summary>Computes the PHI-free call-centre KPIs (phase 15.6) from the service's own tables (call_interaction,
/// caller_verification, appointment_link). Aggregate-only — the query projects counts/ratios, never a member id or
/// any clinical field. Tenant-scoped. The same domain events these tables record are also emitted to the event
/// stream, so reporting-service can build its own read model; this endpoint gives supervisors an immediate view.</summary>
public sealed class KpiService(CallCentreDbContext db)
{
    public async Task<CallKpis> ComputeAsync(string tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var calls = await db.Interactions.AsNoTracking()
            .Where(i => i.TenantId == tenantId && i.StartedAt >= from && i.StartedAt <= to)
            .Select(i => new { i.InteractionId, i.StartedAt, i.EndedAt, i.Status, i.Outcome, i.ReasonCode })
            .ToListAsync(ct);

        var handled = calls.Count;
        var closed = calls.Where(c => c.Status == InteractionStatus.Closed && c.EndedAt is not null).ToList();
        var avgHandle = closed.Count == 0 ? 0d : closed.Average(c => (c.EndedAt!.Value - c.StartedAt).TotalSeconds);
        var resolved = calls.Count(c => c.Outcome == CallOutcome.Resolved);
        var abandoned = calls.Count(c => c.Outcome == CallOutcome.Abandoned);

        var reasonMix = calls.Where(c => c.ReasonCode is not null)
            .GroupBy(c => c.ReasonCode!.Value.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        // Verification-failure rate = failed attempts / all attempts in the window.
        var attempts = await db.Verifications.AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.VerifiedAt >= from && v.VerifiedAt <= to)
            .Select(v => v.Result).ToListAsync(ct);
        var failed = attempts.Count(r => r == VerificationResult.Failed);

        var actions = await db.AppointmentLinks.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.CreatedAt >= from && l.CreatedAt <= to)
            .Select(l => l.Action).ToListAsync(ct);

        return new CallKpis(
            from, to,
            handled,
            avgHandle,
            KpiMath.Ratio(resolved, handled),
            KpiMath.Ratio(failed, attempts.Count),
            KpiMath.Ratio(abandoned, handled),
            actions.Count(a => a == CallAppointmentAction.Book),
            actions.Count(a => a == CallAppointmentAction.Reschedule),
            actions.Count(a => a == CallAppointmentAction.Cancel),
            reasonMix);
    }
}
