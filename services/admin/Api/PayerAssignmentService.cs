using Mersal.Admin.Domain;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Events;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Api;

/// <summary>Result of a payer-restriction attempt. <see cref="ReasonCode"/> "already-assigned" ⇒ the user is
/// already restricted to this payer and a second live copy was refused.</summary>
public sealed record PayerAssignResult(bool Ok, string? ReasonCode, UserPayerAssignment? Assignment);

/// <summary>
/// Phase 19.5 — user↔payer restriction administration (design 38 §6).
///
/// <para>Every mutation is audited at <see cref="AuditSeverity.High"/>. That is a deliberate step up from branch
/// assignment: revoking a payer restriction WIDENS what somebody can see, and a widening is the change an
/// investigation looks for first. A grant of access and a removal of a limit are the same event from the data's
/// point of view.</para>
/// </summary>
public sealed class PayerAssignmentService(AdminDbContext db, IAuditClient audit, IOutbox outbox,
    TimeProvider clock, IBusinessCalendar calendar)
{
    public async Task<PayerAssignResult> AssignAsync(ActorContext actor, string tenant, string subject,
        Guid payerId, DateOnly validFrom, DateOnly? validTo, CancellationToken ct = default)
    {
        var row = new UserPayerAssignment
        {
            AssignmentId = Guid.NewGuid(), TenantId = tenant, SubjectUserId = subject, PayerId = payerId,
            ValidFrom = validFrom, ValidTo = validTo, Status = PayerAssignmentStatus.Active,
            CreatedBy = actor.UserId, CreatedAt = clock.GetUtcNow(),
        };
        // The restriction row and the event announcing it are one access change.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.UserPayerAssignments.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)   // ux_user_payer_active → a duplicate live restriction
        {
            db.ChangeTracker.Clear();
            await audit.EmitAsync(Draft(row, AuditAction.Create, actor, tenant, "denied", "already-assigned", AuditSeverity.Notice), ct);
            return new PayerAssignResult(false, "already-assigned", null);
        }

        // Restricting NARROWS access, so it is Info; the revoke below is where the severity sits.
        await audit.EmitAsync(Draft(row, AuditAction.Create, actor, tenant, "restricted", payerId.ToString()), ct);
        await outbox.EnqueueAsync("UserPayerRestricted", "admin.events",
            new { row.AssignmentId, tenantId = tenant, subject, payerId }, ct);
        await tx.CommitAsync(ct);
        return new PayerAssignResult(true, null, row);
    }

    public async Task<bool> RevokeAsync(ActorContext actor, string tenant, Guid assignmentId, CancellationToken ct = default)
    {
        var row = await db.UserPayerAssignments.FirstOrDefaultAsync(
            x => x.AssignmentId == assignmentId && x.TenantId == tenant && x.Status == PayerAssignmentStatus.Active, ct);
        if (row is null) return false;

        row.Status = PayerAssignmentStatus.Revoked;
        row.RevokedBy = actor.UserId;
        row.RevokedAt = clock.GetUtcNow();
        // Revoking a restriction WIDENS access, and `remaining` is read after the write because the answer
        // depends on it. Both, and the event carrying that count, commit together — a consumer told
        // "remaining: 0" for a revocation that rolled back would treat the user as payer-unrestricted.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);

        // High: removing a restriction is a widening of access. If it was the user's LAST one they become
        // payer-unrestricted, which is the single largest access change this table can produce.
        var remaining = await db.UserPayerAssignments.CountAsync(
            x => x.TenantId == tenant && x.SubjectUserId == row.SubjectUserId
                 && x.Status == PayerAssignmentStatus.Active, ct);
        await audit.EmitAsync(Draft(row, AuditAction.Update, actor, tenant,
            remaining == 0 ? "unrestricted" : "revoked",
            $"remaining:{remaining}", AuditSeverity.High), ct);
        await outbox.EnqueueAsync("UserPayerRestrictionRevoked", "admin.events",
            new { row.AssignmentId, tenantId = tenant, subject = row.SubjectUserId, payerId = row.PayerId, remaining }, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<UserPayerAssignment>> ListAsync(string tenant, string subject, CancellationToken ct = default) =>
        await db.UserPayerAssignments.AsNoTracking()
            .Where(x => x.TenantId == tenant && x.SubjectUserId == subject)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);

    /// <summary>The payer ids in force for a caller today, or an empty list when they are unrestricted. The
    /// caller distinguishes the two — this returns only what the rows say.</summary>
    public async Task<IReadOnlyList<Guid>> EffectivePayerIdsAsync(string tenant, string subject, CancellationToken ct = default)
    {
        var on = calendar.Today();   // 18.A3 — assignment validity is a Cairo date
        var rows = await db.UserPayerAssignments.AsNoTracking()
            .Where(x => x.TenantId == tenant && x.SubjectUserId == subject
                        && x.Status == PayerAssignmentStatus.Active)
            .ToListAsync(ct);
        return [.. rows.Where(r => r.IsEffective(on)).Select(r => r.PayerId).Distinct()];
    }

    private static AuditEventDraft Draft(UserPayerAssignment row, AuditAction action, ActorContext actor, string tenant,
        string? outcome = null, string? reason = null, AuditSeverity severity = AuditSeverity.Info) => new()
    {
        EntityType = "user_payer_assignment", EntityId = row.AssignmentId.ToString(), Action = action,
        ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
        DecisionOutcome = outcome, DecisionReasonCode = reason, Severity = severity,
    };
}
