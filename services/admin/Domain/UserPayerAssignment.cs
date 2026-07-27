namespace Mersal.Admin.Domain;

public enum PayerAssignmentStatus { Active, Revoked }

/// <summary>
/// Phase 19.5 — a user's restriction to one payer's book of business (design 38 §6).
///
/// <para>Lives in admin-service for the same reason <see cref="UserBranchAssignment"/> does: who a person may
/// act for is an identity/administration fact, and keeping it beside role bindings is what makes the phase-16
/// access review able to enumerate a user's entitlements in one place. An entitlement the access review cannot
/// see is an entitlement nobody revokes.</para>
///
/// <para><c>PayerId</c> is a logical reference to <c>policy.payer</c> — a value, not a cross-schema FK, in line
/// with <see cref="UserBranchAssignment.BranchId"/> pointing at <c>provider.branch</c>.</para>
///
/// <para>Unlike a branch assignment there is no Home/Additional distinction: payer scope answers "may you see
/// this" and never "where are you working today", so there is nothing to switch and no active-payer header.
/// Several rows simply union.</para>
/// </summary>
public sealed class UserPayerAssignment
{
    public Guid AssignmentId { get; set; }
    public string TenantId { get; set; } = default!;
    public string SubjectUserId { get; set; } = default!;   // logical FK to identity (value, not FK)
    public Guid PayerId { get; set; }                       // logical FK to policy.payer (value, not FK)
    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
    public PayerAssignmentStatus Status { get; set; } = PayerAssignmentStatus.Active;
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? RevokedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>In force on <paramref name="on"/>: Active and inside its own effective window. A restriction
    /// that has expired stops restricting — it does not linger as a denial nobody can explain.</summary>
    public bool IsEffective(DateOnly on) =>
        Status == PayerAssignmentStatus.Active && ValidFrom <= on && (ValidTo is null || on <= ValidTo.Value);
}
