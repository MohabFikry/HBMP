namespace Mersal.Authz;

/// <summary>
/// The policy-administration overlay (phase 19, design 38 §6). policy-service now carries two very different
/// kinds of write, and collapsing them into one scope would be a real min-necessary failure:
///
/// <list type="bullet">
/// <item><b>Product administration</b> (<see cref="Admin"/>, scope <c>policy:admin</c>) — payers, plans and the
/// effective-dated benefit configuration. This is the new <c>policy_admin</c> role. Authoring a plan version
/// decides what thousands of members are entitled to, so it is deliberately NOT something a Beneficiary
/// Management officer can do while enrolling someone.</item>
/// <item><b>Member administration</b> (<see cref="Write"/>, scope <c>policy:write</c>) — enrolling, terminating
/// and reinstating individual members against an already-authored plan. This is the existing
/// <c>beneficiary_mgmt</c> role and the existing scope, so nothing that works today stops working.</item>
/// </list>
///
/// <see cref="Supervise"/> is the supervisory increment on top of member administration: cancelling ANOTHER
/// user's note (design 38 §5.5) and approving retro-effective enrollment changes. It is separate from
/// <see cref="Admin"/> because a Beneficiary-Management supervisor supervises members, not products.
///
/// Reads are broad on purpose — benefit configuration is the shared vocabulary the whole platform adjudicates
/// against — but a plan version carries no PHI, so a wide read here leaks nothing. Member-level reads and the
/// note bodies are where minimum-necessary actually bites, and those are governed by the 19.3/19.5 rules plus
/// <see cref="FieldProjector"/>, not by this scope.
/// See 10-role-matrix, 11-permission-matrix, 18-security-model.md §4, 19-audit-strategy.md.
/// </summary>
public static class PolicyPolicies
{
    public const string Version = "19.0";

    /// <summary>Read benefit configuration: payers, plans, versions, rules, and the version-in-force resolver.</summary>
    public const string Read = "policy:read";
    /// <summary>Author benefit configuration — create/edit a draft, activate, amend, retire. Policy Administrator.</summary>
    public const string Admin = "policy:admin";
    /// <summary>Member-level administration (policies, groups, enrollments). Beneficiary Management.</summary>
    public const string Write = "policy:write";
    /// <summary>Supervisory increment: cancel another user's note, approve a retro-effective change.</summary>
    public const string Supervise = "policy:supervise";

    public const string Resource = "policy";

    public static IReadOnlyList<PolicyRule> Rules() =>
    [
        // Authoring the benefit product — Policy Administrator (or an org admin acting in that capacity).
        // Sensitive: activating a version changes entitlement platform-wide, so every allow is audited too.
        new PolicyRule
        {
            Action = Admin, ResourceType = Resource,
            Roles = Set("policy_admin", "org_admin", "super_admin"),
            Scopes = Set("policy:admin"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Member administration — the existing Beneficiary Management capability, unchanged.
        new PolicyRule
        {
            Action = Write, ResourceType = Resource,
            Roles = Set("beneficiary_mgmt", "policy_admin", "org_admin", "super_admin"),
            Scopes = Set("policy:write"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        // Supervisory increment over member administration.
        new PolicyRule
        {
            Action = Supervise, ResourceType = Resource,
            Roles = Set("beneficiary_mgmt_supervisor", "org_admin", "super_admin"),
            Scopes = Set("policy:supervise"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Reading the configuration — every role that adjudicates against a benefit needs to see the rules it
        // is being judged by. Tenant-scoped; carries no PHI.
        new PolicyRule
        {
            Action = Read, ResourceType = Resource,
            Scopes = Set("policy:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
    ];

    /// <summary>Full bundle = platform defaults + the policy-administration rules.</summary>
    public static PolicyBundle Bundle()
    {
        var baseBundle = DefaultPolicies.Bundle();
        return new PolicyBundle(Version, [.. baseBundle.Rules, .. Rules()]);
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
