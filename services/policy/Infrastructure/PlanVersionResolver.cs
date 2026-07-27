using Mersal.Policy.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Infrastructure;

/// <summary>Resolves the benefit configuration in force on a given SERVICE DATE — design 38 §7.1, invariant 1.
/// Every consumer (eligibility, authorization, claims) must adjudicate against this rather than "the current
/// version", or a claim submitted late is judged by rules that did not exist when the care was given.</summary>
public interface IPlanVersionResolver
{
    /// <summary>The version of <paramref name="planId"/> whose half-open window contains
    /// <paramref name="serviceDate"/>, with its rules loaded; null when the plan had no configuration in force
    /// that day. Drafts are never resolved — a draft has never been in force.</summary>
    Task<PlanVersion?> ResolveAsync(Guid planId, DateOnly serviceDate, CancellationToken ct = default);
}

public sealed class PlanVersionResolver(PolicyDbContext db) : IPlanVersionResolver
{
    public async Task<PlanVersion?> ResolveAsync(Guid planId, DateOnly serviceDate, CancellationToken ct = default)
    {
        // Superseded and Retired versions ARE resolvable: a past service date lands on exactly those. The
        // 0005 exclusion constraint spans every non-Draft version, so at most one row can match — the
        // resolver has no tie to break, and returning "the latest" would be papering over a broken invariant.
        return await db.PlanVersions.AsNoTracking()
            .Include(v => v.Rules)
            .Where(v => v.PlanId == planId
                        && v.Status != PlanVersionStatus.Draft
                        && v.EffectiveFrom <= serviceDate
                        && (v.EffectiveTo == null || serviceDate < v.EffectiveTo.Value))
            .FirstOrDefaultAsync(ct);
    }
}
