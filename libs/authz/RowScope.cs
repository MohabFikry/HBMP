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

    /// <summary>True when the caller has an unrestricted (global) row scope (e.g. platform super-admin).</summary>
    public bool Unrestricted { get; init; }

    /// <summary>Evaluate the predicate against a candidate row's attributes (server-side gate + test aid).</summary>
    public bool Allows(string? rowTenantId, string? rowProviderId = null, string? rowBeneficiaryId = null)
    {
        if (Unrestricted) return true;
        if (TenantId is not null && !string.Equals(TenantId, rowTenantId, StringComparison.Ordinal)) return false;
        if (ProviderId is not null && !string.Equals(ProviderId, rowProviderId, StringComparison.Ordinal)) return false;
        if (BeneficiaryIds is not null && (rowBeneficiaryId is null || !BeneficiaryIds.Contains(rowBeneficiaryId))) return false;
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
}
