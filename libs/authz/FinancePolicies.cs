namespace Mersal.Authz;

/// <summary>
/// The finance policy overlay (phase 10.2). finance-service produces cost/utilization read-models, provider
/// settlements, financial summaries and audited exports — with a HARD invariant: <b>Finance can NEVER read
/// diagnoses or any clinical detail</b> (11-permission-matrix §3.2 Finance clinical row = all ❌; §4 Finance
/// <c>diagnosis</c> = denied, whole clinical row denied, <c>financials</c> visible, <c>pii</c> masked-min).
///
/// The invariant is enforced in THREE layers: (1) here — Finance holds only the finance actions, and there is NO
/// rule granting Finance any clinical action, so a diagnosis/EMR read is default-denied; (2) the
/// <c>FinanceProjection</c> whitelist in the Domain, which is structurally incapable of carrying a clinical field;
/// (3) the read-model, whose facts carry billing codes + amounts only. Settlement submit/approve are split for
/// segregation of duties (the releaser ≠ the initiator). Every export is a distinct, audited high-severity action.
/// See 10-role-matrix §3.12, 18-security-model.md §4/§8, 19-audit-strategy.md.
/// </summary>
public static class FinancePolicies
{
    public const string Version = "10.0";

    /// <summary>Read utilization (authorized-vs-delivered, spend) — billing codes + amounts only.</summary>
    public const string ReadUtilization = "finance:read-utilization";
    /// <summary>Read / list provider settlements and their priced lines.</summary>
    public const string ReadSettlement = "finance:read-settlement";
    /// <summary>Generate a settlement for a provider + period (initiate).</summary>
    public const string GenerateSettlement = "finance:generate-settlement";
    /// <summary>Submit a settlement for approval (SoD: the initiator step).</summary>
    public const string SubmitSettlement = "finance:submit-settlement";
    /// <summary>Approve a settlement (SoD: MUST be a different principal than the submitter — release step).</summary>
    public const string ApproveSettlement = "finance:approve-settlement";
    /// <summary>Read financial summaries (donor / leadership roll-ups) — amounts by billing code, no diagnosis.</summary>
    public const string ReadSummary = "finance:read-summary";
    /// <summary>Export utilization / settlement / summary — distinct elevated action, masked PII, audited.</summary>
    public const string Export = "finance:export";
    /// <summary>System projection seam — a domain event refreshes the read-model (not a human action).</summary>
    public const string Project = "finance:project";

    public const string Resource = "finance";

    public static IReadOnlyList<PolicyRule> Rules() =>
    [
        new PolicyRule
        {
            Action = ReadUtilization, ResourceType = Resource,
            Roles = Set("finance", "manager", "medical_director"), Scopes = Set("finance:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        new PolicyRule
        {
            Action = ReadSettlement, ResourceType = Resource,
            Roles = Set("finance", "manager"), Scopes = Set("finance:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        new PolicyRule
        {
            Action = GenerateSettlement, ResourceType = Resource,
            Roles = Set("finance"), Scopes = Set("finance:write"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        new PolicyRule
        {
            Action = SubmitSettlement, ResourceType = Resource,
            Roles = Set("finance"), Scopes = Set("finance:write"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        // Release step — a manager / finance approver, distinct principal from the submitter (SoD enforced by the
        // handler comparing submitter vs approver; 11-permission-matrix release rule).
        new PolicyRule
        {
            Action = ApproveSettlement, ResourceType = Resource,
            Roles = Set("finance_approver", "manager", "medical_director"), Scopes = Set("finance:approve"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        new PolicyRule
        {
            Action = ReadSummary, ResourceType = Resource,
            Roles = Set("finance", "manager", "medical_director"), Scopes = Set("finance:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        new PolicyRule
        {
            Action = Export, ResourceType = Resource,
            Roles = Set("finance", "manager", "medical_director"), Scopes = Set("finance:export"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // The projection seam. Roleless on purpose and safe only because `finance:project` is `service_only`
        // in the identity catalogue — the same pairing, and the same history, as `reporting:project`: the
        // scope was person-holdable and granted to `finance`, so the role the cost report is about could
        // write the cost facts it is built from. Revoked by identity 0039; the pairing is asserted by
        // `ProjectionSeamTests`.
        new PolicyRule
        {
            Action = Project, ResourceType = Resource,
            Scopes = Set("finance:project"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
    ];

    /// <summary>Full bundle = platform defaults + the finance rules. finance-service authorizes with this. There is
    /// deliberately NO clinical/diagnosis rule for Finance — the clinical row is default-denied (finance ≠ diagnosis).</summary>
    public static PolicyBundle Bundle()
    {
        var baseBundle = DefaultPolicies.Bundle();
        return new PolicyBundle(Version, [.. baseBundle.Rules, .. Rules()]);
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
