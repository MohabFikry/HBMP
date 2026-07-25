namespace Mersal.Admin.Domain;

/// <summary>Lifecycle of a role binding. A binding is never hard-deleted — a revoke sets <c>Revoked</c> and stamps
/// who/when/why, and a de-provision revokes every binding for the user (soft, auditable).</summary>
public enum BindingStatus { Active, Revoked }

/// <summary>The horizon a granted role is scoped to (mirrors 10-role-matrix §2 scope vocabulary).</summary>
public enum ScopeType { Tenant, Provider, Global }

/// <summary>Sensitivity tier the granted role habitually handles — drives access-review cadence (T3/T4 recertified).</summary>
public enum SensitivityTier { T0, T1, T2, T3, T4 }

/// <summary>
/// A user↔role assignment (Keycloak group membership → app-role claim). Assignment requires a justification and is
/// SoD-checked at grant time; revocation / de-provision immediately withdraws access across all portals. Soft
/// lifecycle only (append revoke metadata, never delete) so the history is auditable.
/// </summary>
public sealed class RoleBinding
{
    public Guid BindingId { get; set; }
    public string TenantId { get; set; } = default!;
    public string SubjectUserId { get; set; } = default!;
    public string Role { get; set; } = default!;
    public ScopeType ScopeType { get; set; } = ScopeType.Tenant;
    public string? ProviderId { get; set; }          // set when ScopeType == Provider
    public SensitivityTier Tier { get; set; } = SensitivityTier.T1;

    public string GrantedBy { get; set; } = default!;
    public string Justification { get; set; } = default!;
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? ReviewDueAt { get; set; }  // next recertification deadline for T3/T4

    public BindingStatus Status { get; set; } = BindingStatus.Active;
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public string? RevokeReason { get; set; }

    public bool IsActiveAt(DateTimeOffset now) => Status == BindingStatus.Active;
}

/// <summary>A user de-provisioned from the platform — the auth layer denies every token/session for this subject.
/// De-provisioning also revokes the user's role bindings; this record makes the block explicit and immediate.</summary>
public sealed class DeprovisionedUser
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = default!;
    public string SubjectUserId { get; set; } = default!;
    public string DeprovisionedBy { get; set; } = default!;
    public string Reason { get; set; } = default!;
    public DateTimeOffset DeprovisionedAt { get; set; }
}

public enum CampaignStatus { Open, Closed }
public enum ReviewDecision { Pending, Recertified, Revoked, AutoExpired }

/// <summary>A periodic access-review campaign (quarterly for T3/T4-reading roles, 19-audit-strategy §7). Each active
/// in-scope binding becomes an item a reviewer must recertify or revoke; unconfirmed items auto-expire at the
/// deadline.</summary>
public sealed class AccessReviewCampaign
{
    public Guid CampaignId { get; set; }
    public string TenantId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public SensitivityTier MinTier { get; set; } = SensitivityTier.T3; // T3 and above are recertified
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = default!;
    public DateTimeOffset DueAt { get; set; }
    public CampaignStatus Status { get; set; } = CampaignStatus.Open;

    public List<AccessReviewItem> Items { get; set; } = [];
}

/// <summary>One grant under review in a campaign. A reviewer confirms (recertify) or withdraws (revoke) need-to-know;
/// if untouched by <see cref="AccessReviewCampaign.DueAt"/> it auto-expires (revokes the binding). Every decision is
/// audited and linked to the binding.</summary>
public sealed class AccessReviewItem
{
    public Guid ItemId { get; set; }
    public Guid CampaignId { get; set; }
    public Guid BindingId { get; set; }
    public string SubjectUserId { get; set; } = default!;
    public string Role { get; set; } = default!;

    public ReviewDecision Decision { get; set; } = ReviewDecision.Pending;
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Note { get; set; }
}

/// <summary>Per-role-tier session policy (18-security-model §9): token lifetime, idle + absolute caps, concurrent
/// limit, and whether step-up MFA is required. Effective-dated so a change never rewrites history.</summary>
public sealed class SessionPolicy
{
    public Guid PolicyId { get; set; }
    public string TenantId { get; set; } = default!;
    public SensitivityTier RoleTier { get; set; }
    public int AccessTokenTtlSeconds { get; set; } = 900;
    public int IdleTimeoutSeconds { get; set; } = 900;
    public int AbsoluteCapSeconds { get; set; } = 28800;
    public int MaxConcurrentSessions { get; set; } = 3;
    public bool StepUpRequired { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public string UpdatedBy { get; set; } = default!;
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Per-role device-compliance + IP-allow-list feeding Conditional Access (18-security-model §3.4–3.5) for
/// admin / Finance / break-glass paths. Effective-dated. IP ranges are stored as a JSON array of CIDR strings.</summary>
public sealed class DevicePolicy
{
    public Guid PolicyId { get; set; }
    public string TenantId { get; set; } = default!;
    public string Role { get; set; } = default!;
    public bool RequireManagedDevice { get; set; }
    public string IpAllowListJson { get; set; } = "[]";
    public DateTimeOffset EffectiveFrom { get; set; }
    public string UpdatedBy { get; set; } = default!;
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum ProposalStatus { Proposed, Approved, Deployed, Rejected }

/// <summary>A staged, versioned change to the ABAC policy bundle. The admin surface PROPOSES + DIFFS — it never
/// hot-patches live policy; deployment goes through the audited CI path (Security + DPO review). Recorded so the
/// diff and its lifecycle are auditable.</summary>
public sealed class PolicyProposal
{
    public Guid ProposalId { get; set; }
    public string BaseVersion { get; set; } = default!;
    public string ProposedVersion { get; set; } = default!;
    public string DiffJson { get; set; } = "{}";
    public string Rationale { get; set; } = default!;
    public ProposalStatus Status { get; set; } = ProposalStatus.Proposed;
    public string ProposedBy { get; set; } = default!;
    public DateTimeOffset ProposedAt { get; set; }
}
