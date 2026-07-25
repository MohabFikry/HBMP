namespace Mersal.Authz;

/// <summary>
/// The pharmacy policy overlay (phase 4.3). A prescriber may CREATE a prescription or a referral only for a
/// patient they treat (US-033/US-034 — same treating-relationship rule as the EMR). Provider-scoped pharmacy
/// dispensing (phase 6) reuses <see cref="ProviderPolicies"/>. pharmacy-service authorizes with <see cref="Bundle"/>.
/// </summary>
public static class PharmacyPolicies
{
    public const string Version = "4.3";

    public const string RxCreate = "rx:create";
    public const string RxRead = "rx:read";
    public const string ReferralCreate = "referral:create";

    public static IReadOnlyList<PolicyRule> Rules() =>
    [
        new PolicyRule
        {
            Action = RxCreate, ResourceType = "prescription",
            Roles = Set("doctor"), Scopes = Set("rx:write"),
            RequiredConditions = [AbacConditions.TenantMatch, AbacConditions.TreatingRelationship],
            Sensitive = true,
        },
        new PolicyRule
        {
            Action = RxRead, ResourceType = "prescription",
            Roles = Set("doctor"), Scopes = Set("rx:read"),
            RequiredConditions = [AbacConditions.TenantMatch, AbacConditions.TreatingRelationship],
            Sensitive = true,
        },
        new PolicyRule
        {
            Action = ReferralCreate, ResourceType = "referral",
            Roles = Set("doctor"), Scopes = Set("referral:write"),
            RequiredConditions = [AbacConditions.TenantMatch, AbacConditions.TreatingRelationship],
            Sensitive = true,
        },
    ];

    /// <summary>Full bundle = platform defaults + provider PO rules (phase-6 dispensing) + prescribe/referral rules.</summary>
    public static PolicyBundle Bundle()
    {
        var providerBundle = ProviderPolicies.Bundle();
        return new PolicyBundle(Version, [.. providerBundle.Rules, .. Rules()]);
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
