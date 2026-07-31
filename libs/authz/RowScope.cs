using Mersal.Auth;

namespace Mersal.Authz;

/// <summary>
/// The row-level primitive: a queryable predicate the data layer composes into its query (aligning with
/// PostgreSQL RLS) so callers only see rows they may see (18-security-model.md §4). Produced from the
/// principal's tenant/provider and (for clinicians) their treating relationships.
/// </summary>
public sealed record RowScope
{
    /// <summary>Restrict to this tenant (always set for tenant-scoped data).</summary>
    public string? TenantId { get; init; }

    /// <summary>Restrict to this provider (provider-scoped users only).</summary>
    public string? ProviderId { get; init; }

    /// <summary>Restrict to these beneficiaries (a treating clinician / assigned case manager).</summary>
    public IReadOnlySet<string>? BeneficiaryIds { get; init; }

    /// <summary>Restrict to these branches (a BranchScoped operational caller). Null ⇒ no branch narrowing
    /// (phase 14, design 37 §3.1 — mirrors the provider-scoping shape).</summary>
    public IReadOnlySet<Guid>? BranchIds { get; init; }

    /// <summary>True when the caller is member-scoped: branch is a convenience filter, never a restriction.</summary>
    public bool BranchUnrestricted { get; init; }

    /// <summary>True when the caller has an unrestricted (global) row scope (e.g. platform super-admin).</summary>
    public bool Unrestricted { get; init; }

    /// <summary>Evaluate the predicate against a candidate row's attributes (server-side gate + test aid).</summary>
    public bool Allows(string? rowTenantId, string? rowProviderId = null, string? rowBeneficiaryId = null, Guid? rowBranchId = null)
    {
        if (Unrestricted) return true;
        if (TenantId is not null && !string.Equals(TenantId, rowTenantId, StringComparison.Ordinal)) return false;
        if (ProviderId is not null && !string.Equals(ProviderId, rowProviderId, StringComparison.Ordinal)) return false;
        if (BeneficiaryIds is not null && (rowBeneficiaryId is null || !BeneficiaryIds.Contains(rowBeneficiaryId))) return false;
        // Branch narrowing applies only to BranchScoped callers (BranchIds set). MemberScoped ⇒ BranchIds null.
        if (BranchIds is not null && (rowBranchId is null || !BranchIds.Contains(rowBranchId.Value))) return false;
        return true;
    }

    /// <summary>Build a tenant + optional provider row scope for a principal.</summary>
    public static RowScope For(HbmpPrincipal p, bool unrestricted = false, IReadOnlySet<string>? beneficiaryIds = null) => new()
    {
        Unrestricted = unrestricted,
        TenantId = unrestricted ? null : p.TenantId,
        ProviderId = p.ProviderId,
        BeneficiaryIds = beneficiaryIds,
    };

    /// <summary>
    /// 21.3 — the branch SENTINEL (design 40 §3): a branch id that can never be a real branch, injected when
    /// a BranchScoped caller's reach cannot be resolved.
    ///
    /// The alternative is an EMPTY predicate, and an empty branch predicate does not mean "nothing" — it
    /// means "every branch in the tenant". That is why this exists as a value rather than as a null check:
    /// the failure mode it prevents is a resolution bug quietly turning into a tenant-wide disclosure of
    /// clinical worklists. A uuid, not a string like '__none__', because every branch column on the platform
    /// is uuid-typed and a string sentinel would have to be coerced somewhere (ADR-0021).
    /// </summary>
    public static readonly Guid NoBranchSentinel = new("00000000-0000-0000-0000-0000000000ff");

    /// <summary>
    /// Layer the branch dimension onto an existing scope (design 37 §3.1, design 40 §3). BranchScoped ⇒
    /// narrow to the active branch; MemberScoped/ProviderScoped ⇒ branch-unrestricted.
    ///
    /// A BranchScoped caller whose active branch did NOT resolve gets the sentinel, so the query returns
    /// zero rows. Before 21.3 this case fell through to <c>BranchUnrestricted = true</c> — a caller who was
    /// supposed to see one branch saw the entire tenant precisely when resolution had gone wrong.
    /// </summary>
    public RowScope WithBranchScope(ScopeMode mode, IBranchContext branch)
    {
        ArgumentNullException.ThrowIfNull(branch);

        // 25.1 (design 42 §1) — the SET form: `branch_id IN (…)` rather than `= active`. The permitted set IS
        // the reach; an active branch NARROWS it (the manager filtered their view to one clinic) and never
        // defines it, which is why clearing the filter restores all six rather than resolving to nothing.
        if (mode == ScopeMode.BranchSetScoped)
        {
            var permitted = branch.PermittedBranchIds;

            // Same fail-closed rule as below, and for the same reason: an EMPTY branch predicate does not mean
            // "nothing", it means "every branch in the tenant". A manager whose grants failed to resolve must
            // see zero rows, not the whole platform.
            if (permitted.Count == 0)
                return this with { BranchIds = new HashSet<Guid> { NoBranchSentinel }, BranchUnrestricted = false };

            // A filter outside the permitted set is NOT silently widened back to the set — the resolver
            // already refuses such a header, and honouring it here would turn a rejected assertion into a
            // quiet grant of everything the caller can reach.
            if (branch.ActiveBranchId is { } filter)
                return this with
                {
                    BranchIds = new HashSet<Guid> { permitted.Contains(filter) ? filter : NoBranchSentinel },
                    BranchUnrestricted = false,
                };

            return this with { BranchIds = new HashSet<Guid>(permitted), BranchUnrestricted = false };
        }

        if (mode != ScopeMode.BranchScoped) return this with { BranchUnrestricted = true };

        var active = branch.ActiveBranchId ?? NoBranchSentinel;
        return this with { BranchIds = new HashSet<Guid> { active }, BranchUnrestricted = false };
    }
}
