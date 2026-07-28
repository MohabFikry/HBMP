namespace Mersal.Authz;

/// <summary>
/// The reporting policy overlay (phase 8.2). reporting-service is a READ-MODEL: aggregate, de-identified KPI views
/// projected from domain events (no row-level PHI, no beneficiary identifiers). Access is split by data zone so the
/// permission matrix (11-permission-matrix §finance ≠ diagnosis) is enforced in AUTHZ, not just in the query:
/// operational KPIs (TAT, pending, workload, utilization, no-show, rejected) and clinical-coded aggregates (top
/// diagnoses / medications) are for the Medical Director / Manager / approvals oversight; FINANCIAL summaries are a
/// separate zone. The finance role holds ONLY the financial action — so a diagnosis-bearing report is default-denied
/// to finance. The projection seam is a system action; every export is audited by the handler. See 07 (US-073),
/// 08 (NFR-006), 18-security-model.md §4, 19-audit-strategy.md.
/// </summary>
public static class ReportingPolicies
{
    public const string Version = "8.2";

    /// <summary>Operational KPI reads — TAT, pending approvals, clinic workload, utilization, no-show, rejected.</summary>
    public const string ReadOperational = "reporting:read-operational";
    /// <summary>Clinical-coded aggregate reads — top diagnoses / medications (coded counts, no PHI). NOT finance.</summary>
    public const string ReadClinical = "reporting:read-clinical";
    /// <summary>Financial-summary reads — service-code/amount aggregates only, NEVER diagnoses.</summary>
    public const string ReadFinancial = "reporting:read-financial";
    /// <summary>System projection seam — a domain event refreshes the read-model (not a human action).</summary>
    public const string Project = "reporting:project";
    /// <summary>Export a report (CSV/PDF) — always audited by the handler.</summary>
    public const string Export = "reporting:export";

    public const string Resource = "report";

    public static IReadOnlyList<PolicyRule> Rules() =>
    [
        new PolicyRule
        {
            Action = ReadOperational, ResourceType = Resource,
            // 19.7 — the benefit-administration roles. 19.6b's dashboard is the analytical layer over the
            // membership book THESE roles administer, and granting `reporting:read` in the identity seed was
            // only half the wiring: this bundle checks the ROLE as well as the scope, so the seed alone
            // produced a token with the right scope and a 403 that said `role-not-permitted`.
            Roles = Set("medical_director", "manager", "medical_approval",
                        "policy_admin", "beneficiary_mgmt", "beneficiary_mgmt_supervisor", "finance"),
            Scopes = Set("reporting:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        // Clinical-coded aggregates — Medical Director / Manager only. Finance has NO rule here → default-denied.
        new PolicyRule
        {
            Action = ReadClinical, ResourceType = Resource,
            Roles = Set("medical_director", "manager"), Scopes = Set("reporting:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        // Financial zone — finance + management.
        new PolicyRule
        {
            Action = ReadFinancial, ResourceType = Resource,
            // Deliberately NOT policy_admin or beneficiary_mgmt: 19.6b's financial and network views are cost
            // per member, net payable and provider value. A benefit author sees enrolment and utilization and
            // must ask Finance for the money — the same zone split phase 8.2 drew, applied rather than
            // re-argued. The seed matches: neither role holds `reporting:read-financial`.
            Roles = Set("finance", "manager", "medical_director"), Scopes = Set("reporting:read-financial"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        // The projection seam — service identity holding the project scope.
        new PolicyRule
        {
            Action = Project, ResourceType = Resource,
            Scopes = Set("reporting:project"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        // Export — management/oversight; the handler writes an Export audit event.
        new PolicyRule
        {
            Action = Export, ResourceType = Resource,
            // 19.6b's dashboard export reuses this action, so the audited-export guarantee covers it too.
            Roles = Set("medical_director", "manager", "finance", "policy_admin"),
            Scopes = Set("reporting:export"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
    ];

    /// <summary>Full bundle = platform defaults + the reporting rules. reporting-service authorizes with this.</summary>
    public static PolicyBundle Bundle()
    {
        var baseBundle = DefaultPolicies.Bundle();
        return new PolicyBundle(Version, [.. baseBundle.Rules, .. Rules()]);
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
