using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Infrastructure;

/// <summary>Read-side queries over the claims store. Returns entities to the Api layer, which maps them to the
/// min-necessary, clinical-free projection DTOs (there is no clinical column to leak — the schema carries codes +
/// amounts only). Tenant is always pinned; provider isolation is applied by the caller (ABAC PO + RLS).</summary>
public sealed class ClaimsQueries(ClaimsDbContext db)
{
    public async Task<IReadOnlyList<Claim>> ListAsync(
        string tenantId, Guid? providerId, Guid? beneficiaryId, ClaimStatus? status, int take, CancellationToken ct)
    {
        var q = db.Claims.AsNoTracking().Include(c => c.Lines).Where(c => c.TenantId == tenantId);
        if (providerId is not null) q = q.Where(c => c.ProviderId == providerId);
        if (beneficiaryId is not null) q = q.Where(c => c.BeneficiaryId == beneficiaryId);
        if (status is not null) q = q.Where(c => c.Status == status);
        return await q.OrderByDescending(c => c.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
    }

    public async Task<Claim?> GetAsync(string tenantId, Guid claimId, CancellationToken ct) =>
        await db.Claims.AsNoTracking().Include(c => c.Lines)
            .FirstOrDefaultAsync(c => c.ClaimId == claimId && c.TenantId == tenantId, ct);
}
