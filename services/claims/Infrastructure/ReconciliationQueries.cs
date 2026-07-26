using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Infrastructure;

/// <summary>A reconciliation worklist row — min-necessary (codes + amounts + linkage, ZERO clinical fields). Each row is
/// one claim line classified into exactly one <see cref="ReconBucket"/> by the pure <see cref="ReconClassifier"/>.</summary>
public sealed record ReconRow(
    Guid ClaimId, string ClaimNo, Guid ClaimLineId, Guid? ProviderId, Guid? ProviderLocationId, string Origin,
    DateOnly ServiceDate, string CodeSystem, string Code, decimal Quantity, decimal BilledAmount,
    decimal? ContractPrice, decimal? AllowedAmount, string LineStatus, string Bucket, Guid? FulfillmentRef);

/// <summary>Builds the reconciliation worklist (10b.7, 36 §7) over the claims schema's own three signals — delivered
/// (a fulfillment anchor / auto-derived origin), billed (a submitted/priced line), and the coded outcomes. Each line is
/// bucketed by <see cref="ReconClassifier"/>. The full three-view cross-service comparison (delivered-not-billed aged
/// feed against live orders/pharmacy) deepens once those fulfillment queries are wired — the classification and
/// precedence live in pure domain code and are unchanged by that wiring.</summary>
public sealed class ReconciliationQueries(ClaimsDbContext db)
{
    public async Task<IReadOnlyList<ReconRow>> ListAsync(
        string tenantId, Guid? providerId, DateOnly from, DateOnly to, string? bucket, decimal? minValue, int take,
        CancellationToken ct = default)
    {
        var q = db.Claims.AsNoTracking().Include(c => c.Lines)
            .Where(c => c.TenantId == tenantId && c.ServiceDateFrom >= from && c.ServiceDateFrom <= to);
        if (providerId is not null) q = q.Where(c => c.ProviderId == providerId);

        var claims = await q.OrderBy(c => c.ServiceDateFrom).Take(Math.Clamp(take, 1, 2000)).ToListAsync(ct);

        var rows = new List<ReconRow>();
        foreach (var c in claims)
        foreach (var l in c.Lines)
        {
            var delivered = l.FulfillmentRef is not null || c.Origin == ClaimOrigin.AutoDerived;
            var billed = c.Origin is ClaimOrigin.ProviderSubmitted or ClaimOrigin.Reimbursement
                         || (c.Origin == ClaimOrigin.AutoDerived && c.Status != ClaimStatus.Draft);
            var isDuplicate = l.ReasonCodes.Contains(ReasonCodes.DuplicateClaim);
            if (l.ReasonCodes.Contains(ReasonCodes.NoFulfillmentRecord)) delivered = false;

            var b = ReconClassifier.Classify(new ReconInput(
                delivered, billed, isDuplicate, l.BilledAmount, l.ContractPrice, null, null));
            if (bucket is not null && !string.Equals(b.ToString(), bucket, StringComparison.OrdinalIgnoreCase)) continue;
            if (minValue is not null && l.BilledAmount < minValue) continue;

            rows.Add(new ReconRow(c.ClaimId, c.ClaimNo, l.ClaimLineId, c.ProviderId, c.ProviderLocationId,
                c.Origin.ToString(), c.ServiceDateFrom, l.CodeSystem.ToString(), l.Code, l.Quantity, l.BilledAmount,
                l.ContractPrice, l.AllowedAmount, l.Status.ToString(), b.ToString(), l.FulfillmentRef));
        }
        return rows;
    }
}
