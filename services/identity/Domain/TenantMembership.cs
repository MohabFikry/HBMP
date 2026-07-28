namespace Mersal.Identity.Domain;

/// <summary>Lifecycle of a <see cref="TenantMembership"/> (design 40 §1). Only <see cref="Active"/> may be
/// selected at login or carried in a token — the other three all mean "not a usable principal right now".</summary>
public enum MembershipStatus
{
    /// <summary>Created, not yet accepted/activated. Cannot authenticate.</summary>
    Invited,

    /// <summary>The only selectable, token-bearing state.</summary>
    Active,

    /// <summary>Temporarily withdrawn (identity disabled, or an administrator suspended this membership).</summary>
    Suspended,

    /// <summary>Terminated. Kept for audit — memberships are never hard-deleted.</summary>
    Ended,
}

/// <summary>
/// The join of an identity to a tenant — and <b>the actual security principal</b> (design 40 §1, invariant 1).
///
/// Authorization evaluates against the active membership, never against the <see cref="ApplicationUser"/>:
/// one identity may hold several memberships with genuinely different authority (a doctor at Mersal who is
/// also a provider-admin at a partner NGO is two memberships, never one blended principal). The membership
/// owns the role bindings, the provider binding, and — from 21.2/21.3 — the per-principal overrides and
/// time-bounded scope grants.
///
/// Table: <c>identity.tenant_membership</c> (Migrations/0010_tenant_membership.sql).
/// </summary>
public sealed class TenantMembership
{
    public Guid MembershipId { get; set; }

    /// <summary>The identity this membership belongs to.</summary>
    public Guid UserId { get; set; }

    /// <summary>The tenant this membership is in. Emitted as the token's <c>tenant_id</c> claim.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Provider ownership for provider-scoped staff — a property of the MEMBERSHIP, because the same
    /// person may be provider-bound in one tenant and ordinary staff in another.</summary>
    public Guid? ProviderId { get; set; }

    public MembershipStatus Status { get; set; } = MembershipStatus.Invited;

    /// <summary>Default branch for this membership; step ③ of 21.3's active-branch precedence chain. A branch
    /// <i>id</i>, not a code — <c>IBranchContext</c> is Guid-keyed throughout (ADR-0021).</summary>
    public Guid? HomeBranchId { get; set; }

    public DateTimeOffset? InvitedAt { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Soft delete — memberships are audit evidence and are never hard-deleted.</summary>
    public bool IsDeleted { get; set; }

    public int RowVersion { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Roles held THROUGH this membership (replaces the identity-level user_role binding).</summary>
    public ICollection<MembershipRole> Roles { get; } = [];

    /// <summary>Whether this membership may be selected at login and minted into a token. Everything except
    /// <see cref="MembershipStatus.Active"/> is unusable, and a soft-deleted row is unusable regardless —
    /// fail closed (invariant 2).</summary>
    public bool IsSelectable => !IsDeleted && Status == MembershipStatus.Active;
}

/// <summary>A role held through a membership. Table: <c>identity.membership_role</c>.</summary>
public sealed class MembershipRole
{
    public Guid MembershipId { get; set; }
    public Guid RoleId { get; set; }
    public string? GrantedBy { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
}

/// <summary>Append-only history twin of <see cref="TenantMembership"/>. Table:
/// <c>identity.tenant_membership_history</c>.</summary>
public sealed class TenantMembershipHistory
{
    public long HistoryId { get; set; }
    public Guid MembershipId { get; set; }
    public Guid UserId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public Guid? ProviderId { get; set; }
    public string Status { get; set; } = string.Empty;
    public Guid? HomeBranchId { get; set; }
    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public string? ChangedBy { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public string? ChangeReason { get; set; }
}
