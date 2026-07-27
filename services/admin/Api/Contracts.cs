using Mersal.Admin.Domain;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Admin.Api;

/// <summary>Request/response contracts for the admin surface. Org Admin omits <c>Tenant</c> (own tenant is used);
/// Super Admin supplies the target <c>Tenant</c> for a cross-tenant action.</summary>
public static class AdminContracts
{
    /// <summary>Build the acting-admin context from the bearer principal.</summary>
    public static ActorContext Actor(HbmpPrincipal p) =>
        new(p.Subject, p.Roles.FirstOrDefault() ?? "unknown", p.TenantId, p.MfaSatisfied);

    /// <summary>The one principal permitted to act outside its own tenant. Roles are flat in the frozen token
    /// contract (docs/security/token-contract.md), so this is a role and not a scope — <c>admin:write</c> is held
    /// by Org Admin too and cannot distinguish global from tenant-local authority.</summary>
    public const string GlobalAdminRole = "super_admin";

    /// <summary>
    /// 18.B2 (audit R2 S-series). Resolve the target tenant for an admin action.
    ///
    /// The old behaviour SILENTLY IGNORED a body-supplied tenant from a non-global admin and substituted the
    /// caller's own: an Org Admin who posted <c>{"tenant":"B", "subjectUserId":…, "role":"clinical_director"}</c>
    /// got a 201 Created and a clinical-director grant — in tenant A, on a user id that means something else
    /// there. The request was denied in substance and reported as success, which is the worst pairing available:
    /// the caller is misinformed, and the audit trail records a grant nobody intended to make. Now it is a 403.
    ///
    /// Naming your OWN tenant is always fine (the SPA does it), so this only rejects a genuine mismatch.
    /// </summary>
    /// <returns>The resolved tenant, or a reason code when the request must be refused.</returns>
    public static TenantResolution ResolveTenantOrDeny(HbmpPrincipal p, string? requested)
    {
        var own = string.IsNullOrWhiteSpace(p.TenantId) ? null : p.TenantId;
        if (string.IsNullOrWhiteSpace(requested))
            return own is null ? TenantResolution.Denied("no-tenant") : TenantResolution.Allowed(own);

        if (string.Equals(requested, own, StringComparison.Ordinal)) return TenantResolution.Allowed(own!);
        if (p.IsInRole(GlobalAdminRole)) return TenantResolution.Allowed(requested);
        return TenantResolution.Denied("cross-tenant-denied");
    }

    /// <summary>Resolve the target tenant: Org Admin is pinned to its own tenant; Super Admin may target any tenant
    /// it names (falling back to its own). Returns null if no tenant can be resolved (bad request).</summary>
    /// <remarks>Read paths only. Write paths must use <see cref="ResolveTenantOrDeny"/> so a mismatched body
    /// tenant is refused rather than quietly redirected at the caller's own tenant.</remarks>
    public static string? ResolveTenant(HbmpPrincipal p, string? requested)
    {
        var resolution = ResolveTenantOrDeny(p, requested);
        return resolution.Tenant;   // a denial reads as "no tenant resolved" → 400, never a redirected write
    }
}

/// <summary>Outcome of <see cref="AdminContracts.ResolveTenantOrDeny"/>: either a tenant to act in, or the reason
/// the request may not proceed. Never both.</summary>
public readonly record struct TenantResolution(string? Tenant, string? ReasonCode)
{
    public static TenantResolution Allowed(string tenant) => new(tenant, null);
    public static TenantResolution Denied(string reasonCode) => new(null, reasonCode);
    public bool IsAllowed => Tenant is not null;

    /// <summary>The refusal to return to the caller. A missing tenant is the client's mistake (400); naming
    /// someone else's tenant is an authorization failure and must read as one (403) — a 400 would invite the
    /// caller to "fix" the request and retry.</summary>
    public IResult ToProblem() => ReasonCode == "cross-tenant-denied"
        ? GateResults.Forbidden("urn:hbmp:cross-tenant-denied",
            detail: "You may only administer your own tenant.", reason: ReasonCode)
        : ProblemResults.Invalid(ReasonCode ?? "no-tenant");
}

// --- 14.2 branch assignment + active-branch context -------------------------------------------
public sealed record AssignBranchRequest(Guid BranchId, string AssignmentType, DateOnly ValidFrom, DateOnly? ValidTo, string? Tenant = null);

public sealed record RevokeBranchRequest(Guid AssignmentId, string? Tenant = null);

public sealed record SwitchBranchRequest(Guid BranchId);

public sealed record BranchAssignmentView(Guid AssignmentId, Guid BranchId, string AssignmentType, DateOnly ValidFrom, DateOnly? ValidTo, string Status)
{
    public static BranchAssignmentView Of(Mersal.Admin.Domain.UserBranchAssignment a) =>
        new(a.AssignmentId, a.BranchId, a.AssignmentType.ToString(), a.ValidFrom, a.ValidTo, a.Status.ToString());
}

public sealed record GrantRoleRequest(string SubjectUserId, string Role, string Justification,
    string? Tenant = null, string Scope = "Tenant", string? ProviderId = null)
{
    public ScopeType ScopeType => Enum.TryParse<ScopeType>(Scope, ignoreCase: true, out var s) ? s : ScopeType.Tenant;
}

public sealed record RevokeRoleRequest(Guid BindingId, string Reason, string? Tenant = null);

public sealed record DeprovisionRequest(string SubjectUserId, string Reason, string? Tenant = null);

public sealed record BindingView(Guid BindingId, string SubjectUserId, string Role, string Scope, string Tier,
    string GrantedBy, DateTimeOffset GrantedAt, DateTimeOffset? ReviewDueAt)
{
    public static BindingView Of(RoleBinding b) =>
        new(b.BindingId, b.SubjectUserId, b.Role, b.ScopeType.ToString(), b.Tier.ToString(),
            b.GrantedBy, b.GrantedAt, b.ReviewDueAt);
}

public sealed record GrantDeniedView(string ReasonCode, IReadOnlyList<SodViolationView> Conflicts);
public sealed record SodViolationView(string HeldRole, string ConflictingRole, string Reason);

public sealed record CreateCampaignRequest(string Name, DateTimeOffset DueAt,
    string MinTier = "T3", string? Tenant = null)
{
    public SensitivityTier Tier => Enum.TryParse<SensitivityTier>(MinTier, ignoreCase: true, out var t) ? t : SensitivityTier.T3;
}

public sealed record ReviewDecisionRequest(string? Note, string? Tenant = null);

public sealed record SessionPolicyRequest(string RoleTier, int AccessTokenTtlSeconds, int IdleTimeoutSeconds,
    int AbsoluteCapSeconds, int MaxConcurrentSessions, bool StepUpRequired, string? Tenant = null)
{
    public SensitivityTier Tier => Enum.TryParse<SensitivityTier>(RoleTier, ignoreCase: true, out var t) ? t : SensitivityTier.T1;
}

public sealed record DevicePolicyRequest(string Role, bool RequireManagedDevice, IReadOnlyList<string> IpAllowList,
    string? Tenant = null);

public sealed record PolicyProposalRequest(string BaseVersion, string ProposedVersion, string DiffJson, string Rationale);
