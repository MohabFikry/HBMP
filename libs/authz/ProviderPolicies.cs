namespace Mersal.Authz;

/// <summary>
/// The reusable provider-network policy overlay (phase 2b). It extends <see cref="DefaultPolicies"/> with
/// the <b>provider-ownership</b> (PO) rules so a provider-scoped user (Provider Admin, Lab/Imaging/Pharmacy
/// tech) can act only on their own provider's resources, while the tenant-scoped Network Team manages
/// provider metadata across the tenant. Downstream services (orders in phase 5, pharmacy in phase 6)
/// import this same bundle so their queues are provider-scoped by exactly the same rule — a bug fixed here
/// is fixed everywhere. See 18-security-model.md §8 and 11-permission-matrix.md (ABAC PO).
/// </summary>
public static class ProviderPolicies
{
    public const string Version = "2b.0";

    // Actions the provider domain authorizes. Reused by fulfillment services for their provider-scoped reads.
    public static class Actions
    {
        public const string Read = "provider:read";
        public const string Write = "provider:write";
        public const string ReadOwn = "provider:read-own";     // Provider Admin, own org only
        public const string QueueRead = "provider-queue:read";  // Lab/Imaging/Pharmacy worklist (phase 5/6)
    }

    /// <summary>The PO rules on their own, so other services can splice them into their bundle.</summary>
    public static IReadOnlyList<PolicyRule> Rules() =>
    [
        // Network Team: manage provider metadata across the tenant (no provider_id, so tenant-match only).
        new PolicyRule
        {
            Action = Actions.Write, ResourceType = "provider",
            Roles = Set("network_team"), Scopes = Set("provider:write"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        new PolicyRule
        {
            Action = Actions.Read, ResourceType = "provider",
            Roles = Set("network_team"), Scopes = Set("provider:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        // Provider Admin: read ONLY their own provider (tenant + provider-ownership).
        new PolicyRule
        {
            Action = Actions.ReadOwn, ResourceType = "provider",
            Roles = Set("provider_admin"), Scopes = Set("provider:read"),
            RequiredConditions = [AbacConditions.TenantMatch, AbacConditions.ProviderOwnership],
            Sensitive = true,
        },
        // Provider techs: read their own provider's work queue only (imported by orders/pharmacy later).
        new PolicyRule
        {
            Action = Actions.QueueRead, ResourceType = "provider_queue",
            Roles = Set("lab_tech", "imaging_tech", "pharmacist", "provider_admin"),
            Scopes = Set("provider:read"),
            RequiredConditions = [AbacConditions.TenantMatch, AbacConditions.ProviderOwnership],
            Sensitive = true,
        },
    ];

    /// <summary>Full bundle = platform defaults + provider PO rules. Provider-service authorizes with this.</summary>
    public static PolicyBundle Bundle()
    {
        var baseBundle = DefaultPolicies.Bundle();
        return new PolicyBundle(Version, [.. baseBundle.Rules, .. Rules()]);
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
