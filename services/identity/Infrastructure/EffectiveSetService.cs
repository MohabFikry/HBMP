using Mersal.Authz;
using Mersal.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Mersal.Identity.Infrastructure;

/// <summary>
/// Mode 2 of the evaluator (design 40 §5) — recompute ANY membership's effective set directly from the
/// store, with no session and no token.
///
/// It exists for three callers that cannot read a token: supervisor-override validation (the approver's
/// right is checked server-side out-of-band, and the acting user's token must NEVER carry the elevated
/// right), background jobs, and the admin "what would this person see" preview.
/// </summary>
public interface IEffectiveSetService
{
    /// <summary>The effective set for a membership, recomputed from the store (cached briefly).</summary>
    Task<EffectiveSet?> ForMembershipAsync(Guid membershipId, CancellationToken ct = default);

    /// <summary>Drop the cached set for a membership. Called on EVERY grant mutation — role change,
    /// override change, scope-grant change, membership suspension.</summary>
    void Invalidate(Guid membershipId);
}

/// <summary>
/// Loads the inputs and runs <see cref="EffectiveSetEvaluator"/>. BOTH modes go through this one type:
/// mode 1 (token issuance) calls <see cref="ComputeAsync"/> with the membership already in hand, mode 2
/// calls <see cref="ForMembershipAsync"/> with only an id. They share the loader AND the algebra, which is
/// what makes the parity suite a real check rather than a comparison of two copies of the same bug.
/// </summary>
public sealed class EffectiveSetService(
    IdentityStoreDbContext db,
    RoleScopeResolver resolver,
    MembershipService memberships,
    DeprecationReporter deprecation,
    TimeProvider clock,
    IMemoryCache cache) : IEffectiveSetService
{
    /// <summary>
    /// Mode-2 cache lifetime. Short on purpose: this is the out-of-session path, so a stale answer here is
    /// an authorization decision made on withdrawn authority. Explicit invalidation on mutation is the
    /// primary mechanism; the TTL is the backstop for anything that forgets to invalidate.
    /// </summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private static string Key(Guid membershipId) => $"authz:effective:{membershipId:N}";

    public async Task<EffectiveSet?> ForMembershipAsync(Guid membershipId, CancellationToken ct = default)
    {
        if (cache.TryGetValue(Key(membershipId), out EffectiveSet? hit) && hit is not null) return hit;

        var membership = await db.Memberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.MembershipId == membershipId && !m.IsDeleted, ct);
        if (membership is null) return null;

        var result = await ComputeAsync(membership, "effective-set-service", ct);
        cache.Set(Key(membershipId), result, CacheTtl);
        return result;
    }

    public void Invalidate(Guid membershipId) => cache.Remove(Key(membershipId));

    /// <summary>
    /// The shared path. Assembles the snapshot — tenant-resolved role grants, live overrides, and the
    /// identity's platform-admin flag — and runs the one algebra over it.
    /// </summary>
    /// <param name="consumer">Who is resolving, for the deprecation report. Naming the consumer is what
    /// turns "this key is still alive somewhere" into "these callers have to move".</param>
    public async Task<EffectiveSet> ComputeAsync(
        TenantMembership membership, string consumer, CancellationToken ct = default)
    {
        var roles = await memberships.RolesForAsync(membership.MembershipId, ct);
        var roleGrants = await resolver.ResolveScopesAsync(roles, membership.TenantId, ct);

        // Soft-deleted overrides are filtered in SQL; EXPIRY is deliberately left to the evaluator so both
        // modes apply one definition of "live" against one injected clock.
        var overrides = await db.Overrides.AsNoTracking()
            .Where(o => o.MembershipId == membership.MembershipId && !o.IsDeleted)
            .Select(o => new { o.ScopeKey, o.Effect, o.ValidUntil })
            .ToListAsync(ct);

        var isPlatformAdmin = await db.Users.AsNoTracking()
            .Where(u => u.Id == membership.UserId).Select(u => u.IsPlatformAdmin).FirstOrDefaultAsync(ct);

        var catalog = await CatalogAsync(ct);

        var result = EffectiveSetEvaluator.Compute(
            new MembershipSnapshot(
                [.. roleGrants],
                [.. overrides.Select(o => new OverrideEntry(o.ScopeKey, o.Effect == OverrideEffect.Deny, o.ValidUntil))],
                isPlatformAdmin),
            catalog,
            clock.GetUtcNow());

        if (result.DeprecatedInUse.Count > 0) deprecation.Report(consumer, result.DeprecatedInUse);
        return result;
    }

    /// <summary>The catalog metadata, cached for the same short window — it changes only by migration or by
    /// an administrator editing the catalog, and both invalidate through the ordinary mutation path.</summary>
    public async Task<IReadOnlyDictionary<string, CatalogKey>> CatalogAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CatalogCacheKey, out IReadOnlyDictionary<string, CatalogKey>? hit) && hit is not null)
            return hit;

        var rows = await db.Scopes.AsNoTracking()
            .Select(s => new { s.Name, s.Deprecated, s.ReplacedBy, s.IsPlatformAdminKey })
            .ToListAsync(ct);

        var catalog = rows.ToDictionary(
            r => r.Name,
            r => new CatalogKey(r.Name, r.Deprecated, r.ReplacedBy, r.IsPlatformAdminKey),
            StringComparer.Ordinal);

        cache.Set(CatalogCacheKey, (IReadOnlyDictionary<string, CatalogKey>)catalog, CacheTtl);
        return catalog;
    }

    /// <summary>Invalidate the cached catalog — after any change to scope metadata.</summary>
    public void InvalidateCatalog() => cache.Remove(CatalogCacheKey);

    private const string CatalogCacheKey = "authz:catalog";
}
