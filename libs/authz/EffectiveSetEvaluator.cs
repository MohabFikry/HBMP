namespace Mersal.Authz;

/// <summary>One catalog key as the evaluator needs to see it (design 40 §2 + §6).</summary>
/// <param name="Key">The scope key, e.g. <c>orders:read</c>.</param>
/// <param name="Deprecated">Superseded but still resolving — a migration signal, not an enforcement one.</param>
/// <param name="ReplacedBy">The key that supersedes it, when deprecated.</param>
/// <param name="IsPlatformAdminKey">A1 — governs platform administration, and is therefore the only kind of
/// key the platform-admin flag may short-circuit.</param>
public sealed record CatalogKey(
    string Key, bool Deprecated = false, string? ReplacedBy = null, bool IsPlatformAdminKey = false);

/// <summary>A per-membership exception, as the evaluator sees it.</summary>
/// <param name="Key">The catalog key allowed or denied.</param>
/// <param name="Deny">True to withhold the key; false to grant it. Deny always wins.</param>
/// <param name="ValidUntil">When it stops applying; null means indefinite.</param>
public sealed record OverrideEntry(string Key, bool Deny, DateTimeOffset? ValidUntil = null);

/// <summary>
/// Everything the effective set is computed FROM, in one value — so that both modes provably feed the same
/// algebra the same shape of input, and the parity suite has something to build fixtures out of.
/// </summary>
/// <param name="RoleGrants">Keys granted by the membership's roles (already tenant-resolved).</param>
/// <param name="Overrides">Per-membership allows and denies.</param>
/// <param name="IsPlatformAdmin">Whether the underlying identity carries the platform-administration flag.</param>
public sealed record MembershipSnapshot(
    IReadOnlyCollection<string> RoleGrants,
    IReadOnlyCollection<OverrideEntry> Overrides,
    bool IsPlatformAdmin = false);

/// <summary>A deprecated key that was actually resolved, reported so umbrella-splits are driven by evidence.</summary>
/// <param name="Key">The deprecated key.</param>
/// <param name="ReplacedBy">What to move to, if the catalog says.</param>
public sealed record DeprecationUse(string Key, string? ReplacedBy);

/// <summary>The computed authority, plus what the caller should log.</summary>
/// <param name="Keys">The effective key set.</param>
/// <param name="DeprecatedInUse">Deprecated keys present in the result.</param>
public sealed record EffectiveSet(IReadOnlySet<string> Keys, IReadOnlyList<DeprecationUse> DeprecatedInUse)
{
    public bool Has(string key) => Keys.Contains(key);
}

/// <summary>
/// THE authority algebra (design 40 §2 + §5, invariant 5). One implementation, two entry points:
/// identity-service calls it at token issuance (mode 1) and <c>IEffectiveSetService</c> calls it from the
/// store out-of-session (mode 2). A parity suite runs the same fixture matrix through both and fails on any
/// divergence, because two copies of this rule drifting apart is the standing risk the design names.
///
///     effective = (role grants ∪ membership allows) − membership denies
///
/// Deny wins, always. Expired overrides are inert. Deprecated keys still resolve. The platform-admin flag
/// short-circuits ONLY keys the catalog marks as platform-administration keys (A1).
/// </summary>
public static class EffectiveSetEvaluator
{
    /// <summary>
    /// Compute the effective key set for a membership.
    /// </summary>
    /// <param name="snapshot">The grants, overrides and platform-admin flag to evaluate.</param>
    /// <param name="catalog">The catalog keys, by key. Keys absent from the catalog still resolve — the
    /// catalog carries METADATA, and treating an unknown key as absent would let a catalog row that failed
    /// to seed silently revoke live access.</param>
    /// <param name="asOf">The instant expiry is judged against. Injected, never read from the wall clock,
    /// so mode 1 and mode 2 can be compared at a fixed point.</param>
    public static EffectiveSet Compute(
        MembershipSnapshot snapshot, IReadOnlyDictionary<string, CatalogKey> catalog, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(catalog);

        // Only overrides that are live at `asOf` participate. An expired Allow stops granting, and an
        // expired Deny stops withholding — a time-boxed restriction is meant to end too.
        var live = snapshot.Overrides.Where(o => o.ValidUntil is null || o.ValidUntil > asOf).ToArray();

        // Deny is collected FIRST and applied last so that no ordering of the inputs can let an Allow
        // outrank it. Both an Allow override and a role grant lose to a Deny on the same key.
        var denies = live.Where(o => o.Deny).Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        var allows = live.Where(o => !o.Deny).Select(o => o.Key);

        var keys = new HashSet<string>(snapshot.RoleGrants, StringComparer.Ordinal);
        keys.UnionWith(allows);
        keys.ExceptWith(denies);

        // A1 — the platform-admin short-circuit, and the reason it is safe. It adds ONLY keys the catalog
        // marks as platform-administration keys; every other key is hard-excluded, so this can never become
        // a wildcard over clinical, benefit or financial data no matter what the catalog gains later.
        //
        // Note it is applied AFTER the denies: a platform administrator whose membership explicitly denies
        // an administration key does not get it back. An override is a deliberate act by another
        // administrator, and silently overturning it would make the override surface untrustworthy.
        if (snapshot.IsPlatformAdmin)
            foreach (var admin in catalog.Values.Where(c => c.IsPlatformAdminKey && !denies.Contains(c.Key)))
                keys.Add(admin.Key);

        var deprecated = keys
            .Select(k => catalog.TryGetValue(k, out var c) ? c : null)
            .Where(c => c is { Deprecated: true })
            .Select(c => new DeprecationUse(c!.Key, c.ReplacedBy))
            .OrderBy(d => d.Key, StringComparer.Ordinal)
            .ToArray();

        return new EffectiveSet(keys, deprecated);
    }
}
