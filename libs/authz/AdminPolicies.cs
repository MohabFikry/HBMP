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

    // ---- Phase 8b.2 governance actions (master data / templates / system config) ----
    /// <summary>Effective-dated master-data edit (ICD/CPT/LOINC/Drug/ATC/interactions/allergens/formulary).
    /// Restricted to clinical governance (FR-MDM-008).</summary>
    public const string EditMasterData = "admin:edit-masterdata";
    /// <summary>
    /// READ the governed master-data versions — the list, and a code as-of a date.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its own action rather than <see cref="ReadAccess"/>, which is held by the platform admins alone and
    /// also gates the access matrix, the SoD matrix, break-glass and the access-review campaigns. A Medical
    /// Director reading the ICD table is not a Medical Director reading who can do what on the platform, and
    /// one action covering both would have to choose which of those two audiences to be wrong about.
    /// </para>
    /// <para>
    /// It exists because ADR-0035 §4 gave clinical governance the master-data EDITOR while the list behind it
    /// still answered to the admin-only read — an editor over a list its own author could not open. Granting
    /// the write and forgetting the read is the same defect as granting the authority and giving it no door,
    /// which is the thing that ADR set out to fix.
    /// </para>
    /// </remarks>
    public const string ReadMasterData = "admin:read-masterdata";
    /// <summary>Manage bilingual notification templates (PHI-safe linter enforced).</summary>
    public const string EditTemplate = "admin:edit-template";
    /// <summary>Manage tenant/platform system configuration (typed, validated, effective-dated).</summary>
    public const string EditConfig = "admin:edit-config";
    /// <summary>
    /// Set how long a prescription or an investigation order stays actionable before it expires.
    ///
    /// <para>A SEPARATE action from <see cref="EditConfig"/>, and deliberately held by clinical governance
    /// rather than by the platform admins. How long a prescription remains safe to dispense is a clinical
    /// judgement about how fast a patient's condition moves, not a system setting — the Medical Director who
    /// supervises the approval queue is the person who lives with the consequence of getting it wrong, and
    /// is the one the extension requests land on when it is too short.</para>
    /// </summary>
    public const string EditValidityPolicy = "admin:edit-validity-policy";

    // ---- Phase 8b.3 tenant/provider governance, break-glass, dashboards ----
    /// <summary>Manage tenants + platform-wide config (Super Admin only — FR-IAM-008).</summary>
    public const string ManageTenant = "admin:manage-tenant";
    /// <summary>Request a break-glass grant (any authorized clinical/admin caller may originate).</summary>
    public const string BreakGlassRequest = "admin:break-glass-request";
    /// <summary>Approve a break-glass grant (dual control: a second authorized approver ≠ requester).</summary>
    public const string BreakGlassApprove = "admin:break-glass-approve";
    /// <summary>View the audit / access-review / break-glass dashboards (the view is itself audited).</summary>
    public const string ReadDashboard = "admin:read-dashboard";

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
        // Master-data governance — clinical governance (Medical Director) + Super Admin only (FR-MDM-008). Org
        // Admin is NOT a master-data editor. Master data is a global reference surface → tenant null (global).
        new PolicyRule
        {
            Action = ReadMasterData, ResourceType = Resource,
            // The editors, plus the platform admins who already read everything else here.
            Roles = Set("medical_director", "org_admin", "super_admin"), Scopes = Set("admin:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        new PolicyRule
        {
            Action = EditMasterData, ResourceType = Resource,
            Roles = Set("medical_director", "super_admin"), Scopes = Set("admin:write"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Validity periods — clinical governance (Medical Director) + Super Admin. Same audience as master
        // data for the same reason: it is a clinical safety parameter that happens to be stored as config.
        new PolicyRule
        {
            Action = EditValidityPolicy, ResourceType = Resource,
            Roles = Set("medical_director", "super_admin"), Scopes = Set("admin:write"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Notification-template governance — clinical governance + admins (bilingual, PHI-safe linter).
        new PolicyRule
        {
            Action = EditTemplate, ResourceType = Resource,
            Roles = Set("medical_director", "super_admin", "org_admin"), Scopes = Set("admin:write"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // System configuration — admins.
        new PolicyRule
        {
            Action = EditConfig, ResourceType = Resource,
            Roles = Set(Admins), Scopes = Set("admin:write"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Tenant administration — Super Admin only (FR-IAM-008; global surface → tenant null).
        new PolicyRule
        {
            Action = ManageTenant, ResourceType = Resource,
            Roles = Set("super_admin"), Scopes = Set("admin:write"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Break-glass request — a broad set of clinical/oversight roles may ORIGINATE (dual-control gates approval).
        new PolicyRule
        {
            Action = BreakGlassRequest, ResourceType = Resource,
            Roles = Set("doctor", "nurse", "medical_approval", "medical_director", "case_manager", "org_admin", "super_admin"),
            Scopes = Set("admin:break-glass"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Break-glass approval — a distinct authorized approver tier (dual control enforced in the handler too).
        new PolicyRule
        {
            Action = BreakGlassApprove, ResourceType = Resource,
            Roles = Set("medical_director", "org_admin", "super_admin"),
            Scopes = Set("admin:break-glass"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Dashboards — admins + oversight; the view is Sensitive → the read is audited.
        new PolicyRule
        {
            Action = ReadDashboard, ResourceType = Resource,
            Roles = Set("org_admin", "super_admin", "medical_director"), Scopes = Set("admin:read"),
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
