namespace Mersal.Authz;

/// <summary>
/// The admin / platform-management policy overlay (phase 8b). Org Admin (tenant-scoped) and Super Admin (global)
/// administer WHO can access the platform — role bindings, session/device/IP policy, access-review campaigns, and
/// staged policy-bundle proposals — they are NOT routine readers of beneficiary PHI/financial CONTENT (that is
/// break-glass only). Every admin action here is Sensitive → the allow is audited (grants, revocations, config
/// changes, review decisions), and viewing the access matrix is itself an audited read (19-audit-strategy §7).
/// Org Admin operates <c>tenant:own</c> (TenantMatch); Super Admin operates <c>global</c> — the gate sets the
/// resource tenant to null for a Super-Admin cross-tenant action so TenantMatch is satisfied without widening Org
/// Admin. SoD is enforced separately at grant time via <see cref="SegregationOfDuties"/>. See 10-role-matrix §3.15/§3.16.
/// </summary>
public static class AdminPolicies
{
    public const string Version = "8b.1";

    /// <summary>View users / role bindings / the access matrix / review campaigns — an audited admin READ.</summary>
    public const string ReadAccess = "admin:read-access";
    /// <summary>Assign a role binding to a user (SoD-checked, justification-required, audited).</summary>
    public const string GrantRole = "admin:grant-role";
    /// <summary>Revoke a role binding / de-provision a user across all portals (audited).</summary>
    public const string RevokeRole = "admin:revoke-role";
    /// <summary>Change session / device / IP-allow-list / system configuration (audited, effective-dated).</summary>
    public const string Configure = "admin:configure";
    /// <summary>Stage / diff a policy-bundle proposal (proposes only; never hot-patches live ABAC).</summary>
    public const string ProposePolicy = "admin:propose-policy";
    /// <summary>Recertify or revoke a grant in an access-review campaign (audited, linked to the grant).</summary>
    public const string Review = "admin:review";

    public const string Resource = "admin";

    /// <summary>The admin audiences. Super Admin additionally carries the global scope for cross-tenant actions.</summary>
    private static readonly string[] Admins = ["org_admin", "super_admin"];

    public static IReadOnlyList<PolicyRule> Rules() =>
    [
        new PolicyRule
        {
            Action = ReadAccess, ResourceType = Resource,
            Roles = Set(Admins), Scopes = Set("admin:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true, // who viewed the access matrix is itself audited
        },
        new PolicyRule
        {
            Action = GrantRole, ResourceType = Resource,
            Roles = Set(Admins), Scopes = Set("admin:write"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        new PolicyRule
        {
            Action = RevokeRole, ResourceType = Resource,
            Roles = Set(Admins), Scopes = Set("admin:write"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        new PolicyRule
        {
            Action = Configure, ResourceType = Resource,
            Roles = Set(Admins), Scopes = Set("admin:write"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Proposing a policy bundle is Super-Admin only (global ABAC surface); it stages a diff, never deploys.
        new PolicyRule
        {
            Action = ProposePolicy, ResourceType = Resource,
            Roles = Set("super_admin"), Scopes = Set("admin:write"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        new PolicyRule
        {
            Action = Review, ResourceType = Resource,
            Roles = Set(Admins), Scopes = Set("admin:write"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
    ];

    /// <summary>Full bundle = platform defaults + the admin rules. admin-service authorizes with this.</summary>
    public static PolicyBundle Bundle()
    {
        var baseBundle = DefaultPolicies.Bundle();
        return new PolicyBundle(Version, [.. baseBundle.Rules, .. Rules()]);
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
