namespace Mersal.Authz;

/// <summary>
/// The ABAC condition codes (18-security-model.md §4). Each policy rule may require one or more;
/// the engine reports which were satisfied on an allow.
/// </summary>
public static class AbacConditions
{
    public const string TenantMatch = "tenant-match";
    public const string ProviderOwnership = "provider-ownership";
    public const string TreatingRelationship = "treating-relationship";
    public const string ResourceStatusActive = "resource-status-active";
    public const string BreakGlass = "break-glass";

    /// <summary>Tenant isolation: principal.tenant == resource.tenant (or resource has no tenant scope).</summary>
    public static bool TenantMatches(AuthzRequest r) =>
        r.Resource.TenantId is null || string.Equals(r.Principal.TenantId, r.Resource.TenantId, StringComparison.Ordinal);

    /// <summary>Provider-ownership: the resource belongs to the caller's provider.</summary>
    public static bool ProviderOwns(AuthzRequest r) =>
        r.Resource.ProviderId is not null
        && string.Equals(r.Principal.ProviderId, r.Resource.ProviderId, StringComparison.Ordinal);

    /// <summary>Treating-relationship: the caller treats the beneficiary this resource concerns.</summary>
    public static bool HasTreatingRelationship(AuthzRequest r) =>
        r.Resource.BeneficiaryId is not null
        && r.Resource.TreatingBeneficiaryIds.Contains(r.Resource.BeneficiaryId);
}
