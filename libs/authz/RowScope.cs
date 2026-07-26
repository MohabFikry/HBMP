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

    /// <summary>Layer the branch dimension onto an existing scope (design 37 §3.1). BranchScoped ⇒ narrow to the
    /// active branch; MemberScoped/ProviderScoped ⇒ branch-unrestricted. Never widens the other dimensions.</summary>
    public RowScope WithBranchScope(ScopeMode mode, IBranchContext branch) =>
        mode == ScopeMode.BranchScoped && branch.ActiveBranchId is { } active
            ? this with { BranchIds = new HashSet<Guid> { active } }
            : this with { BranchUnrestricted = true };
}
