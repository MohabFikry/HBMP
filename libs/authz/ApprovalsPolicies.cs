namespace Mersal.Authz;

/// <summary>
/// The medical-approvals policy overlay (phase 7). The Medical Approval team and Medical Director review
/// authorization requests. Reads are tenant-scoped OVERSIGHT (no treating relationship — the approver did not
/// treat the patient, 11-permission-matrix §3.2): the worklist projection carries no clinical payload, while the
/// review DTO (the ONLY clinical-context endpoint) is field-scoped and PHI-read audited under purpose PUR.
/// Break-glass paths (emergency approve / director override / manual authorization) are Director-only and each
/// requires extra justification (enforced in the handler) with a specially-flagged audit trail. approvals-service
/// authorizes with <see cref="Bundle"/>. See 18-security-model.md §4, 19-audit-strategy.md, 23-state-machines §5.
/// </summary>
public static class ApprovalsPolicies
{
    public const string Version = "7.0";

    /// <summary>Reviewer inbox / worklist read — min-necessary projection, no clinical fields.</summary>
    public const string List = "auth:list";
    /// <summary>Pick up a Submitted request (Submitted → UnderReview), starting the SLA timer.</summary>
    public const string Assign = "auth:assign";
    /// <summary>The clinical review view — EMR summary + notes + supporting documents, field-scoped, PHI-audited.</summary>
    public const string Review = "auth:review";
    /// <summary>Decide (approve / partial / reject / request-info / resupply) — phase 7.2.</summary>
    public const string Decide = "auth:decide";
    /// <summary>Emergency fast-track (Submitted → EmergencyApproved), Director only — phase 7.3.</summary>
    public const string Emergency = "auth:emergency";
    /// <summary>Director override of a rejection (Rejected → Overridden), Director only — phase 7.3.</summary>
    public const string Override = "auth:override";
    /// <summary>Manual authorization created without a provider submission — phase 7.3.</summary>
    public const string Manual = "auth:manual";

    public const string Resource = "authorization";

    public static IReadOnlyList<PolicyRule> Rules() =>
    [
        // Worklist inbox — tenant-scoped, no clinical payload → not flagged sensitive.
        new PolicyRule
        {
            Action = List, ResourceType = Resource,
            Roles = Set("medical_approval", "medical_director"), Scopes = Set("auth:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        // Pick up a request — state change only, tenant-scoped.
        new PolicyRule
        {
            Action = Assign, ResourceType = Resource,
            Roles = Set("medical_approval", "medical_director"), Scopes = Set("auth:review"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        // The clinical review view — tenant-scoped oversight read; PHI, so audited even on allow (purpose PUR).
        new PolicyRule
        {
            Action = Review, ResourceType = Resource,
            Roles = Set("medical_approval", "medical_director"), Scopes = Set("auth:review"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Decisions (phase 7.2) — tenant-scoped; the decision writes an immutable rationale-bearing record.
        new PolicyRule
        {
            Action = Decide, ResourceType = Resource,
            Roles = Set("medical_approval", "medical_director"), Scopes = Set("auth:decide"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Break-glass: emergency approval — Director authority only (phase 7.3).
        new PolicyRule
        {
            Action = Emergency, ResourceType = Resource,
            Roles = Set("medical_director"), Scopes = Set("auth:emergency"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Break-glass: director override of a rejection — Director authority only (phase 7.3).
        new PolicyRule
        {
            Action = Override, ResourceType = Resource,
            Roles = Set("medical_director"), Scopes = Set("auth:override"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Manual authorization — Medical Approval / Director (phase 7.3).
        new PolicyRule
        {
            Action = Manual, ResourceType = Resource,
            Roles = Set("medical_approval", "medical_director"), Scopes = Set("auth:manual"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
    ];

    /// <summary>Full bundle = platform defaults + the approvals rules. approvals-service authorizes with this.</summary>
    public static PolicyBundle Bundle()
    {
        var baseBundle = DefaultPolicies.Bundle();
        return new PolicyBundle(Version, [.. baseBundle.Rules, .. Rules()]);
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
