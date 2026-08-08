namespace Mersal.Authz;

/// <summary>
/// The ABAC condition codes (18-security-model.md §4). Each policy rule may require one or more;
/// the engine reports which were satisfied on an allow.
/// </summary>
public static class AbacConditions
{
    public const string TenantMatch = "tenant-match";
    public const string ProviderOwnership = "provider-ownership";
    public const string TreatingRelationship = "treating-relationship";
    public const string ResourceStatusActive = "resource-status-active";
    public const string BreakGlass = "break-glass";

    /// <summary>Case-assignment (phase 10): a Case Manager may act on a case (and reach that beneficiary's
    /// coordination view) ONLY while they hold an ACTIVE assignment to it. Unassignment revokes it (10 §3.11).</summary>
    public const string CaseAssignment = "case-assignment";

    /// <summary>Branch-scope (phase 14, design 37 §3): a BranchScoped operational caller (reception, nurse,
    /// doctor worklists, branch manager) may act on a resource only when its <c>branch_id</c> is in the caller's
    /// permitted set AND equals the active branch. It NARROWS — never replaces the other conditions (a doctor
    /// still needs treating-relationship). MemberScoped roles omit this condition entirely.</summary>
    public const string BranchScope = "branch-scope";

    /// <summary>Tenant isolation: principal.tenant == resource.tenant (or resource has no tenant scope).</summary>
    public static bool TenantMatches(AuthzRequest r) =>
        r.Resource.TenantId is null || string.Equals(r.Principal.TenantId, r.Resource.TenantId, StringComparison.Ordinal);

    /// <summary>Provider-ownership: the resource belongs to the caller's provider.</summary>
    public static bool ProviderOwns(AuthzRequest r) =>
        r.Resource.ProviderId is not null
        && string.Equals(r.Principal.ProviderId, r.Resource.ProviderId, StringComparison.Ordinal);

    /// <summary>Treating-relationship: the caller treats the beneficiary this resource concerns.</summary>
    public static bool HasTreatingRelationship(AuthzRequest r) =>
        r.Resource.BeneficiaryId is not null
        && r.Resource.TreatingBeneficiaryIds.Contains(r.Resource.BeneficiaryId);

    /// <summary>Case-assignment: the caller holds an active assignment to the case being acted on. The case id is
    /// carried as <see cref="ResourceRef.Id"/>; the caller's active-assignment set is resolved (from the
    /// <c>case_assignment</c> rows) into <see cref="ResourceRef.AssignedCaseIds"/> before evaluation — mirroring how
    /// treating-relationship is resolved. Unassignment empties the set → immediate revocation.</summary>
    public static bool HasCaseAssignment(AuthzRequest r) =>
        r.Resource.Id is not null && r.Resource.AssignedCaseIds.Contains(r.Resource.Id);

    /// <summary>Branch-scope: the resource's branch is in the caller's permitted set and (when an active branch
    /// is set) equals it. The permitted set + active branch are resolved onto the <see cref="ResourceRef"/>
    /// before evaluation, mirroring how treating-relationship / case-assignment are resolved.
    ///
    /// 25.1 — membership of the permitted set is the invariant and holds in BOTH reach modes; what changes is
    /// the active branch. Under <see cref="ScopeMode.BranchSetScoped"/> it is a view FILTER, so it is not
    /// applied here: a clinics manager who narrowed their screen to one clinic has not thereby resigned as
    /// supervisor of the others (see <see cref="ResourceRef.BranchReach"/>). The set-membership test is never
    /// relaxed by the mode — an unresolved set is empty and denies, as it must.</summary>
    public static bool InBranchScope(AuthzRequest r) =>
        r.Resource.BranchId is { } b
        && r.Resource.PermittedBranchIds.Contains(b)
        && (r.Resource.BranchReach == ScopeMode.BranchSetScoped
            || r.Resource.ActiveBranchId is null || r.Resource.ActiveBranchId == b);
}
