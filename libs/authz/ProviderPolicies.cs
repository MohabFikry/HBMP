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

        /// <summary>19.1b — create/retire a network tier and move providers between tiers. Deliberately
        /// separate from <see cref="Write"/>: ordinary provider metadata (a new address, another credential)
        /// is routine Network Team work, whereas a tier reassignment reprices every plan that references that
        /// tier, for every member, from its effective date.</summary>
        public const string NetworkAdmin = "network-tier:admin";

        /// <summary>14.5 — read the clinician picker (who works at this branch, in which specialty).
        /// Deliberately separate from <see cref="Read"/>: booking needs the practitioner list and nothing
        /// else, whereas <c>provider:read</c> is the whole network directory — contracts, onboarding state
        /// and tiers — which the front desk has no business seeing. Sized to the need, as
        /// <c>patient:read</c> was.</summary>
        public const string PractitionerRead = "practitioner:read";
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
        // 19.1b — network administration. THE SEPARATION 19.1b EXISTS TO CREATE: a policy administrator
        // consumes tiers when configuring cost-share (policy.benefit_rule_tier) but must not be able to move a
        // provider between them, because moving one provider from OON to T1 silently reprices every plan that
        // references those tiers, for every member, from the assignment's effective date. Network commercial
        // policy belongs to the Network Team; benefit design belongs to policy administration.
        // Sensitive: an allow is as interesting as a deny when the act repricings a whole network.
        new PolicyRule
        {
            Action = Actions.NetworkAdmin, ResourceType = "network_tier",
            Roles = Set("network_team", "org_admin", "super_admin"),
            Scopes = Set("provider:admin"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // 14.5 — the clinician picker. The roles that BOOK (reception, call centre) and the clinical roles
        // that read who a visit belongs to. Not sensitive: it is a name and a specialty, the same pair
        // printed on the clinic door.
        new PolicyRule
        {
            Action = Actions.PractitionerRead, ResourceType = "practitioner",
            // org_admin is deliberately absent: it administers ACCESS, not the clinical picker, and holds
            // neither scope in the seed — naming it would have made this the 142nd rule/role pair that can
            // never be satisfied, which is the exact defect ScopeIntegrityTests exists to catch.
            Roles = Set("reception", "call_center", "doctor", "nurse", "case_manager", "network_team"),
            Scopes = Set("practitioner:read", "provider:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
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
