using Mersal.Authz;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Infrastructure;

/// <summary>
/// 21.4 — reads the per-tenant programme switches and enforces the caps (design 40 §4, migration 0008).
///
/// Features are cheap to read and ride in the token per 21.0. Caps are NOT cached and never will be: they
/// are counts of live rows, and a cached count is a wrong count the moment anything changes.
/// </summary>
public sealed class TenantProgramStore(AdminDbContext db)
{
    /// <summary>Every feature switch for a tenant. Keys absent from the result are DISABLED — the caller
    /// uses <see cref="ProgramEnablement.IsEnabled"/> rather than indexing, so a missing row cannot be
    /// mistaken for an enabled one.</summary>
    public async Task<IReadOnlyDictionary<string, bool>> FeaturesAsync(string tenantId, CancellationToken ct = default)
    {
        var rows = await db.Database
            .SqlQueryRaw<FeatureRow>(
                "SELECT feature_key AS \"Key\", enabled AS \"Enabled\" FROM admin.tenant_feature WHERE tenant_id = {0}",
                tenantId)
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Key, r => r.Enabled, StringComparer.Ordinal);
    }

    /// <summary>The configured cap, or null when none is set (which means unlimited — see
    /// <see cref="ProgramEnablement.WouldBreach"/> for why that direction).</summary>
    public async Task<int?> LimitAsync(string tenantId, string limitKey, CancellationToken ct = default)
    {
        var rows = await db.Database
            .SqlQueryRaw<int>(
                "SELECT max_value AS \"Value\" FROM admin.tenant_limit WHERE tenant_id = {0} AND limit_key = {1}",
                tenantId, limitKey)
            .ToListAsync(ct);

        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>
    /// Take the per-(tenant, limit) serialization lock for the CURRENT TRANSACTION.
    ///
    /// This is the part that makes live counting correct under concurrency, and the part that is easy to
    /// leave out because single-threaded tests never notice. Two parallel creates at cap−1 each run
    /// `SELECT count(*)`, each see N−1 under READ COMMITTED because neither has committed yet, and both
    /// insert — so the tenant ends up at N+1 and the cap silently did nothing. A transaction-scoped
    /// advisory lock serializes just this check, for just this tenant and limit, and is released
    /// automatically on commit OR rollback, so a failed request cannot leave the lock held.
    ///
    /// It is keyed on the pair, so two different tenants — or two different caps for one tenant — never
    /// block each other.
    /// </summary>
    public Task LockForLimitAsync(string tenantId, string limitKey, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtext({0}), hashtext({1}))", [tenantId, limitKey], ct);

    /// <summary>
    /// Whether one more row may be created, counted LIVE inside the caller's transaction.
    ///
    /// <paramref name="liveCount"/> must run the real count against the real table in the SAME transaction —
    /// that is what makes "delete a user and the slot frees immediately" true by construction instead of by
    /// remembering to decrement something.
    /// </summary>
    /// <returns>Null when the mutation may proceed; otherwise the problem to return.</returns>
    public async Task<Microsoft.AspNetCore.Http.IResult?> CheckLimitAsync(
        string tenantId, string limitKey, Func<CancellationToken, Task<int>> liveCount, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(liveCount);

        var max = await LimitAsync(tenantId, limitKey, ct);
        if (max is null) return null;   // unlimited — no lock needed, and no reason to serialize anything

        await LockForLimitAsync(tenantId, limitKey, ct);
        var current = await liveCount(ct);

        return ProgramEnablement.WouldBreach(max, current)
            ? ProgramEnablement.LimitReached(limitKey, max.Value, current)
            : null;
    }

    private sealed record FeatureRow(string Key, bool Enabled);
}
