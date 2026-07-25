namespace Mersal.Admin.Domain;

/// <summary>Break-glass grant lifecycle (18-security-model §11). Requested → (dual-control) Approved → (step-up)
/// Active → Expired/Revoked, or Rejected. Access is only widened while Active AND within the scoped window.</summary>
public enum BreakGlassStatus { Requested, Approved, Active, Rejected, Expired, Revoked }

/// <summary>
/// A break-glass grant record — the phase-8b storage the runtime <c>libs/authz/BreakGlassGrant</c> is projected
/// from. Emergency, scoped, time-boxed access widening: a mandatory reason code + justification, a distinct
/// second-approver (dual control, SoD-enforced), a short auto-expiring window, step-up MFA to activate, and
/// loud high-severity audit on every access under it. It NEVER enables self-approval nor disables field-level
/// denies beyond its explicit scope.
/// </summary>
public sealed class BreakGlassGrantRecord
{
    public Guid GrantId { get; set; }
    public string TenantId { get; set; } = default!;
    public string RequesterUserId { get; set; } = default!;
    public string ReasonCode { get; set; } = default!;
    public string Justification { get; set; } = default!;

    /// <summary>Resource types this grant is scoped to (e.g. "encounter"). Access outside is NOT widened.</summary>
    public string ScopedResourceTypesJson { get; set; } = "[]";
    /// <summary>Specific resource ids the grant is scoped to (empty = any id of a scoped type).</summary>
    public string ScopedResourceIdsJson { get; set; } = "[]";

    public int WindowMinutes { get; set; } = 60;
    public BreakGlassStatus Status { get; set; } = BreakGlassStatus.Requested;

    public string? ApproverUserId { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? RejectReason { get; set; }

    public bool StepUpSatisfied { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? NotBefore { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    public string? RevokedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset RequestedAt { get; set; }
    public bool PostReviewDone { get; set; }   // mandatory post-hoc review

    public bool IsActiveAt(DateTimeOffset now) =>
        Status == BreakGlassStatus.Active && NotBefore is not null && ExpiresAt is not null
        && now >= NotBefore && now < ExpiresAt;
}

/// <summary>A single access performed under an active break-glass grant — emitted as a high-severity audit event
/// and surfaced on the dashboard for mandatory post-hoc review.</summary>
public sealed class BreakGlassAccess
{
    public Guid AccessId { get; set; }
    public Guid GrantId { get; set; }
    public string TenantId { get; set; } = default!;
    public string ActorUserId { get; set; } = default!;
    public string ResourceType { get; set; } = default!;
    public string? ResourceId { get; set; }
    public string Action { get; set; } = default!;
    public bool WithinScope { get; set; }
    public DateTimeOffset AccessedAt { get; set; }
}

/// <summary>A platform tenant (Mersal = tenant 0; future orgs/donors as tenants). Super Admin manages tenants;
/// every domain row carries tenant_id and RLS prevents cross-tenant leakage.</summary>
public sealed class Tenant
{
    public string TenantId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public bool Active { get; set; } = true;
    public string CreatedBy { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
}
