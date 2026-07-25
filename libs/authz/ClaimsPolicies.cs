namespace Mersal.Authz;

/// <summary>
/// The claims policy overlay (Phase 10b). claims-service turns delivered, authorized services into reviewed, decided
/// and settled financial records. HARD invariant, identical in spirit to Finance: <b>Claims can NEVER read diagnoses
/// or any clinical detail</b> (11-permission-matrix §3.2 <c>Finance/Claims → diagnosis = denied</c>). The invariant
/// holds in three layers: (1) here — the claims roles hold only the claims actions, and there is NO rule granting
/// any clinical action, so a diagnosis/EMR read is default-denied; (2) server-side allow-list projection DTOs that
/// are structurally incapable of carrying a clinical field; (3) the schema itself carries codes + amounts only.
///
/// Roles: <b>Claims Officer</b> (worklist + line decisions) and <b>Claims Reviewer / Senior</b> (dual-control
/// approvals, overrides, batch decide/void). Segregation of duties is enforced at the SERVICE, not here: the decider
/// is never the originator and never provider-affiliated, and adjudication is separate from settlement release
/// (10-role-matrix §3, 11-permission-matrix §6.7). Provider reads are provider-isolated (ABAC PO + RLS).
/// </summary>
public static class ClaimsPolicies
{
    public const string Version = "10b.0";

    /// <summary>Read / list claims + lines (min-necessary projection — codes, amounts, no clinical fields).</summary>
    public const string ReadClaim = "claims:read";
    /// <summary>Claims-officer worklist read (financial + PHI-adjacent — audited on read).</summary>
    public const string Review = "claims:review";
    /// <summary>Record a line-level decision (append-only; SoD + dual-control enforced in the handler).</summary>
    public const string Decide = "claims:decide";
    /// <summary>Run automated pre-adjudication for a claim (10b.3).</summary>
    public const string Adjudicate = "claims:adjudicate";
    /// <summary>Raise an append-only adjustment on a line (10b.7).</summary>
    public const string Adjust = "claims:adjust";
    /// <summary>Create / manage batches (10b.2).</summary>
    public const string Batch = "claims:batch";
    /// <summary>Reconciliation worklist (10b.7).</summary>
    public const string Reconcile = "claims:reconcile";
    /// <summary>Provider-submitted claim intake (10b.5).</summary>
    public const string Submit = "claims:submit";
    /// <summary>Beneficiary reimbursement submission (10b.6).</summary>
    public const string ReimburseSubmit = "claims:reimburse:submit";
    /// <summary>Appeal a decided claim (10b.9).</summary>
    public const string Appeal = "claims:appeal";
    /// <summary>Export a settlement advice / batch — distinct elevated, audited action (10b.8).</summary>
    public const string Export = "claims:export";
    /// <summary>Record an EXTERNAL settlement / payment reference (SoD-split from decide) (10b.8).</summary>
    public const string Settle = "claims:settle";
    /// <summary>System intake seam — an auto-derive event creates a claim line (not a human action).</summary>
    public const string Ingest = "claims:ingest";

    public const string Resource = "claim";

    public static IReadOnlyList<PolicyRule> Rules() =>
    [
        new PolicyRule
        {
            Action = ReadClaim, ResourceType = Resource,
            Roles = Set("claims_officer", "claims_reviewer", "manager", "finance"), Scopes = Set("claims:read"),
            // Provider users may read only their own claims (ABAC provider-ownership); Mersal staff read tenant-wide.
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        new PolicyRule
        {
            Action = Review, ResourceType = Resource,
            Roles = Set("claims_officer", "claims_reviewer"), Scopes = Set("claims:review"),
            RequiredConditions = [AbacConditions.TenantMatch], Sensitive = true,
        },
        new PolicyRule
        {
            Action = Decide, ResourceType = Resource,
            Roles = Set("claims_officer", "claims_reviewer"), Scopes = Set("claims:decide"),
            RequiredConditions = [AbacConditions.TenantMatch], Sensitive = true,
        },
        new PolicyRule
        {
            Action = Adjudicate, ResourceType = Resource,
            Roles = Set("claims_officer", "claims_reviewer"), Scopes = Set("claims:adjudicate"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        new PolicyRule
        {
            Action = Adjust, ResourceType = Resource,
            Roles = Set("claims_officer", "claims_reviewer"), Scopes = Set("claims:adjust"),
            RequiredConditions = [AbacConditions.TenantMatch], Sensitive = true,
        },
        new PolicyRule
        {
            Action = Batch, ResourceType = Resource,
            Roles = Set("claims_officer", "claims_reviewer"), Scopes = Set("claims:batch"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        new PolicyRule
        {
            Action = Reconcile, ResourceType = Resource,
            Roles = Set("claims_officer", "claims_reviewer"), Scopes = Set("claims:reconcile"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        new PolicyRule
        {
            Action = Submit, ResourceType = Resource,
            Roles = Set("claims_officer", "provider_admin", "network_manager"), Scopes = Set("claims:submit"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        new PolicyRule
        {
            Action = ReimburseSubmit, ResourceType = Resource,
            Roles = Set("claims_officer", "reception", "case_manager"), Scopes = Set("claims:reimburse:submit"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        new PolicyRule
        {
            Action = Appeal, ResourceType = Resource,
            Roles = Set("claims_officer", "case_manager", "provider_admin"), Scopes = Set("claims:appeal"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        // Release step — SoD-separated from decide (a settlement releaser is never the batch creator/decider).
        new PolicyRule
        {
            Action = Export, ResourceType = Resource,
            Roles = Set("claims_reviewer", "finance", "manager"), Scopes = Set("claims:export"),
            RequiredConditions = [AbacConditions.TenantMatch], Sensitive = true,
        },
        new PolicyRule
        {
            Action = Settle, ResourceType = Resource,
            Roles = Set("finance", "manager"), Scopes = Set("claims:settle"),
            RequiredConditions = [AbacConditions.TenantMatch], Sensitive = true,
        },
        new PolicyRule
        {
            Action = Ingest, ResourceType = Resource,
            Scopes = Set("claims:ingest"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
    ];

    /// <summary>Full bundle = platform defaults + the claims rules. claims-service authorizes with this. There is
    /// deliberately NO clinical/diagnosis rule — the clinical row is default-denied (claims ≠ diagnosis).</summary>
    public static PolicyBundle Bundle()
    {
        var baseBundle = DefaultPolicies.Bundle();
        return new PolicyBundle(Version, [.. baseBundle.Rules, .. Rules()]);
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
