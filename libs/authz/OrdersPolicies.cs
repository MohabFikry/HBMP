namespace Mersal.Authz;

/// <summary>
/// The investigation-orders policy overlay (phase 4.2). A doctor may CREATE an order only for a patient they
/// treat (US-032, the same treating-relationship rule as the EMR); ordering also carries the treating half fed
/// from the row-level check (here, emr-service's treating probe). Provider-scoped discovery/consume of active
/// orders (phase 5) reuses <see cref="ProviderPolicies"/>. orders-service authorizes with <see cref="Bundle"/>.
/// </summary>
public static class OrdersPolicies
{
    public const string Version = "4.2";

    public const string Create = "orders:create";
    public const string Read = "orders:read";

    public static IReadOnlyList<PolicyRule> Rules() =>
    [
        // Doctor creates an investigation order — tenant + treating relationship.
        new PolicyRule
        {
            Action = Create, ResourceType = "investigation_order",
            Roles = Set("doctor"), Scopes = Set("orders:write"),
            RequiredConditions = [AbacConditions.TenantMatch, AbacConditions.TreatingRelationship],
            Sensitive = true,
        },
        // Ordering doctor may read back their patient's order — tenant + treating relationship.
        new PolicyRule
        {
            Action = Read, ResourceType = "investigation_order",
            Roles = Set("doctor"), Scopes = Set("orders:read"),
            RequiredConditions = [AbacConditions.TenantMatch, AbacConditions.TreatingRelationship],
            Sensitive = true,
        },
    ];

    /// <summary>Full bundle = platform defaults + provider PO rules (phase-5 reads) + order-create rules.</summary>
    public static PolicyBundle Bundle()
    {
        var providerBundle = ProviderPolicies.Bundle();
        return new PolicyBundle(Version, [.. providerBundle.Rules, .. Rules()]);
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
