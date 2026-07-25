namespace Mersal.Authz;

/// <summary>
/// The case-management policy overlay (phase 10.1). case-service coordinates care/benefit over an ASSIGNED case
/// load: a Case Manager may read/act on a case — and reach that beneficiary's coordination-360 view — ONLY while
/// they hold an active <c>case_assignment</c> (10 §3.11 "unassignment revokes it"). This is encoded as the
/// <see cref="AbacConditions.CaseAssignment"/> ABAC condition, resolved at the policy layer (the gate loads the
/// caller's active assignments into <see cref="ResourceRef.AssignedCaseIds"/>), NOT merely in the controller.
///
/// The beneficiary-360 assembly is a coordination CLINICAL SUMMARY: per 11-permission-matrix §4 a Case Manager
/// gets <c>diagnosis visible(coord)</c> but emr_note / prescription / lab_result / imaging_result are masked at
/// summary level — enforced by the field-scoped DTO in case-service, and audited as a PHI read on every assembly.
/// Assigning/unassigning is a supervisory action (Manager / Medical Director) — not the Case Manager themselves.
/// See 10-role-matrix §3.11, 18-security-model.md §4, 19-audit-strategy.md.
/// </summary>
public static class CasePolicies
{
    public const string Version = "10.0";

    /// <summary>Read a case / My-Cases worklist entry — assignment-scoped for the Case Manager.</summary>
    public const string Read = "case:read";
    /// <summary>Supervisory oversight read — a Manager / Medical Director reads a case WITHOUT a treating/assignment
    /// relationship (a distinct action because the engine matches one rule per action+resource; the gate selects it
    /// by role). Kept distinct so the two read purposes are separately auditable.</summary>
    public const string ReadOversight = "case:read-oversight";
    /// <summary>Assemble the beneficiary-360 coordination view (field-scoped, PHI-read audited) — assignment-scoped.</summary>
    public const string Read360 = "case:read-360";
    /// <summary>Write a case / coordination task / escalation — assignment-scoped for the Case Manager.</summary>
    public const string Write = "case:write";
    /// <summary>Open a new case (supervisory / intake — no prior assignment needed).</summary>
    public const string Open = "case:open";
    /// <summary>Assign or unassign a Case Manager to a case (supervisory: Manager / Medical Director).</summary>
    public const string Manage = "case:manage";

    public const string Resource = "case";

    /// <summary>The case rules on their own (spliceable). Read/Read360/Write require an active case-assignment for
    /// the Case Manager; supervisory roles (manager / medical_director) reach the case for oversight without one.</summary>
    public static IReadOnlyList<PolicyRule> Rules() =>
    [
        // Case Manager reads an ASSIGNED case — tenant + active assignment.
        new PolicyRule
        {
            Action = Read, ResourceType = Resource,
            Roles = Set("case_manager"), Scopes = Set("case:read"),
            RequiredConditions = [AbacConditions.TenantMatch, AbacConditions.CaseAssignment],
        },
        // Supervisory oversight read — tenant only, no assignment required (distinct action, gate-selected by role).
        new PolicyRule
        {
            Action = ReadOversight, ResourceType = Resource,
            Roles = Set("manager", "medical_director"), Scopes = Set("case:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        // Beneficiary-360 coordination assembly — Case Manager, tenant + active assignment. Sensitive (PHI-read).
        new PolicyRule
        {
            Action = Read360, ResourceType = Resource,
            Roles = Set("case_manager"), Scopes = Set("case:read"),
            RequiredConditions = [AbacConditions.TenantMatch, AbacConditions.CaseAssignment],
            Sensitive = true,
        },
        // Case Manager writes a coordination task / escalation / case update — tenant + active assignment.
        new PolicyRule
        {
            Action = Write, ResourceType = Resource,
            Roles = Set("case_manager"), Scopes = Set("case:write"),
            RequiredConditions = [AbacConditions.TenantMatch, AbacConditions.CaseAssignment],
        },
        // Open a new case — Case Manager (intake) or supervisory; no prior assignment (the case doesn't exist yet).
        new PolicyRule
        {
            Action = Open, ResourceType = Resource,
            Roles = Set("case_manager", "manager", "medical_director"), Scopes = Set("case:write"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        // Assign / unassign — supervisory only (the assignment is the access anchor; a manager can't self-grant).
        new PolicyRule
        {
            Action = Manage, ResourceType = Resource,
            Roles = Set("manager", "medical_director"), Scopes = Set("case:manage"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
    ];

    /// <summary>Full bundle = platform defaults + the case rules. case-service authorizes with this.</summary>
    public static PolicyBundle Bundle()
    {
        var baseBundle = DefaultPolicies.Bundle();
        return new PolicyBundle(Version, [.. baseBundle.Rules, .. Rules()]);
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
