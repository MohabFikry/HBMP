using Mersal.Auth;

namespace Mersal.Admin.Domain;

/// <summary>Phase 14.2 — a staff member's assignment to a Mersal branch (design 37 §2.2). Assignment is an
/// identity/administration concern, so it lives in admin-service alongside role bindings. Exactly one active
/// <c>Home</c> per user (enforced by a partial-unique index); <c>Additional</c> rows grant the ability to
/// work elsewhere. Soft-lifecycle: a revoke stamps metadata (never deleted) and takes effect on the next
/// request. The permitted set (Home ∪ Additional, effective) drives branch scoping in 14.3+.</summary>
public sealed class UserBranchAssignment
{
    public Guid AssignmentId { get; set; }
    public string TenantId { get; set; } = default!;
    public string SubjectUserId { get; set; } = default!;   // logical FK to identity (value, not FK)
    public Guid BranchId { get; set; }                       // logical FK to provider.branch (value, not FK)
    public BranchAssignmentType AssignmentType { get; set; }
    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
    public BranchAssignmentStatus Status { get; set; } = BranchAssignmentStatus.Active;
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? RevokedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Project to the pure-rules value type.</summary>
    public BranchAssignment ToAssignment() => new(BranchId, AssignmentType, ValidFrom, ValidTo, Status);
}
