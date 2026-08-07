namespace Mersal.Authz;

/// <summary>
/// The investigation-orders policy overlay (phase 4.2). A doctor may CREATE an order only for a patient they
/// treat (US-032, the same treating-relationship rule as the EMR); ordering also carries the treating half fed
/// from the row-level check (here, emr-service's treating probe). Provider-scoped discovery/consume of active
/// orders (phase 5) reuses <see cref="ProviderPolicies"/>. orders-service authorizes with <see cref="Bundle"/>.
/// </summary>
public static class OrdersPolicies
{
    public const string Version = "5.0";

    public const string Create = "orders:create";
    public const string Read = "orders:read";

    /// <summary>Phase 5: a fulfilling provider (lab/imaging tech) consumes an order line for their own provider.</summary>
    public const string Consume = "orders:consume-line";

    /// <summary>Phase 5: the ordering doctor (treating) or the approval team reads an uploaded result.</summary>
    public const string ReadResult = "orders:read-result";

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
        // Phase 5: a lab/imaging tech consumes a line for their OWN provider (tenant + provider-ownership); the
        // Lab-vs-Imaging capability match is a domain check on the order type. Default-deny for everyone else
        // (a doctor cannot consume, a tech of the wrong capability is refused in the handler).
        new PolicyRule
        {
            Action = Consume, ResourceType = "order_line",
            Roles = Set("lab_tech", "imaging_tech", "radiology_tech"), Scopes = Set("orders:consume"),
            RequiredConditions = [AbacConditions.TenantMatch, AbacConditions.ProviderOwnership],
            Sensitive = true,
        },
        // Phase 5.3: the ordering doctor (treating) reads their patient's result. The approval team's oversight
        // read is a distinct action (below) so this rule can carry the treating condition (11-permission-matrix:
        // results visible to ordering doctor + approval team, not unrelated roles).
        new PolicyRule
        {
            Action = ReadResult, ResourceType = "investigation_order",
            Roles = Set("doctor"), Scopes = Set("orders:read"),
            RequiredConditions = [AbacConditions.TenantMatch, AbacConditions.TreatingRelationship],
            Sensitive = true,
        },
        // Phase 5.3: the medical-approval team may read any result in the tenant for oversight (no treating gate).
        new PolicyRule
        {
            Action = ReadResult, ResourceType = "order_result",
            Roles = Set("approvals_team", "medical_director"), Scopes = Set("orders:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
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
