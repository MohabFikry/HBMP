using Mersal.Admin.Domain;
using Mersal.Auth;

namespace Mersal.Admin.Api;

/// <summary>Request/response contracts for the admin surface. Org Admin omits <c>Tenant</c> (own tenant is used);
/// Super Admin supplies the target <c>Tenant</c> for a cross-tenant action.</summary>
public static class AdminContracts
{
    /// <summary>Build the acting-admin context from the bearer principal.</summary>
    public static ActorContext Actor(HbmpPrincipal p) =>
        new(p.Subject, p.Roles.FirstOrDefault() ?? "unknown", p.TenantId, p.MfaSatisfied);

    /// <summary>Resolve the target tenant: Org Admin is pinned to its own tenant; Super Admin may target any tenant
    /// it names (falling back to its own). Returns null if no tenant can be resolved (bad request).</summary>
    public static string? ResolveTenant(HbmpPrincipal p, string? requested)
    {
        if (p.IsInRole("super_admin")) return requested ?? p.TenantId;
        return p.TenantId; // org_admin ignores a requested tenant — always its own
    }
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
