using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Infrastructure;

/// <summary>Assembles the claims KPI aggregate (10b.9, 36 §11) from the claims schema and computes it with the pure
/// <see cref="ClaimsKpiCalculator"/>. AGGREGATE-ONLY and de-identified: no clinical fields, no direct identifiers beyond
/// provider (for the variance league). reporting-service (phase 8) consumes this; dashboards are not duplicated here.</summary>
public sealed class KpiQueries(ClaimsDbContext db)
{
    public async Task<ClaimsKpi> ComputeAsync(string tenantId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var claims = await db.Claims.AsNoTracking().Include(c => c.Lines)
            .Where(c => c.TenantId == tenantId && c.ServiceDateFrom >= from && c.ServiceDateFrom <= to).ToListAsync(ct);

        var claimFacts = claims.Select(c => new DecidedClaimFact(
            c.ProviderId, c.Status, c.SubmittedAt, c.DecidedAt, c.ApprovedAmount ?? 0m, c.ClaimedAmount,
            c.Status == ClaimStatus.Denied ? c.Lines.SelectMany(l => l.ReasonCodes).ToList() : [])).ToList();

        var adjustments = await db.ClaimAdjustments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && !a.PendingSecondApproval)
            .Select(a => new AdjustmentFact(a.AdjustmentType, a.AmountDelta)).ToListAsync(ct);

        var reimbursements = await db.ReimbursementRequests.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.MatchMethod != ReimbursementMatchMethod.Unmatched)
            .Select(r => new ReimbursementFact(r.MatchMethod)).ToListAsync(ct);

        // Aged unbilled = delivered-not-billed: auto-derived claims a provider has not yet submitted (still Draft).
        var unbilled = claims
            .Where(c => c.Origin == ClaimOrigin.AutoDerived && c.Status == ClaimStatus.Draft)
            .SelectMany(c => c.Lines.Select(l => new UnbilledFact(c.ProviderId, l.BilledAmount))).ToList();

        return ClaimsKpiCalculator.Compute(claimFacts, adjustments, reimbursements, unbilled);
    }
}
