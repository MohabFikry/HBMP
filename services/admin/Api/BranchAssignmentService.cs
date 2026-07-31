using Mersal.Admin.Domain;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;
using Mersal.Time;

namespace Mersal.Admin.Api;

/// <summary>Result of an assignment attempt. <see cref="ReasonCode"/> "home-exists" ⇒ a second active Home
/// was rejected (design 37 §2.2).</summary>
public sealed record AssignResult(bool Ok, string? ReasonCode, UserBranchAssignment? Assignment);

/// <summary>Phase 14.2 — staff↔branch assignment administration + active-branch resolution. Assignments are
/// SoD-neutral identity data; every mutation is audited and emits a domain event. The active-branch resolver
/// enforces THE INVARIANT: a requested branch outside the permitted set is denied and audited
/// (BranchScopeDenied). Assignments are soft-lifecycle (revoke stamps metadata, effective next request).</summary>
public sealed class BranchAssignmentService(AdminDbContext db, IAuditClient audit, IOutbox outbox, TimeProvider clock,
    IBusinessCalendar calendar)
{
    public async Task<AssignResult> AssignAsync(ActorContext actor, string tenant, string subject, Guid branchId,
        BranchAssignmentType type, DateOnly validFrom, DateOnly? validTo, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var row = new UserBranchAssignment
        {
            AssignmentId = Guid.NewGuid(), TenantId = tenant, SubjectUserId = subject, BranchId = branchId,
            AssignmentType = type, ValidFrom = validFrom, ValidTo = validTo, Status = BranchAssignmentStatus.Active,
            CreatedBy = actor.UserId, CreatedAt = now,
        };
        // An assignment is what a caller's branch scope is computed from, so a row that commits without its
        // event leaves every consumer resolving scope from a set it never heard change.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.UserBranchAssignments.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) // ux_user_home_branch → a second active Home
        {
            db.ChangeTracker.Clear();
            await audit.EmitAsync(Draft(row, AuditAction.Create, actor, tenant, "denied", "home-exists", AuditSeverity.Notice), ct);
            return new AssignResult(false, "home-exists", null);
        }

        await audit.EmitAsync(Draft(row, AuditAction.Create, actor, tenant, "assigned", type.ToString()), ct);
        await outbox.EnqueueAsync("UserBranchAssigned", "admin.events",
            new { row.AssignmentId, tenantId = tenant, subject, branchId, assignmentType = type.ToString() }, ct);
        await tx.CommitAsync(ct);
        return new AssignResult(true, null, row);
    }

    public async Task<bool> RevokeAsync(ActorContext actor, string tenant, Guid assignmentId, CancellationToken ct = default)
    {
        var row = await db.UserBranchAssignments
            .FirstOrDefaultAsync(x => x.AssignmentId == assignmentId && x.TenantId == tenant && x.Status == BranchAssignmentStatus.Active, ct);
        if (row is null) return false;

        row.Status = BranchAssignmentStatus.Revoked;
        row.RevokedBy = actor.UserId;
        row.RevokedAt = clock.GetUtcNow();
        // A revocation that commits without UserBranchRevoked is access removed here and still granted
        // everywhere downstream — the failure mode this rule exists for, in its least visible form.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);

        await audit.EmitAsync(Draft(row, AuditAction.Update, actor, tenant, "revoked"), ct);
        await outbox.EnqueueAsync("UserBranchRevoked", "admin.events",
            new { row.AssignmentId, tenantId = tenant, subject = row.SubjectUserId, branchId = row.BranchId }, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<UserBranchAssignment>> ListAsync(string tenant, string subject, CancellationToken ct = default) =>
        await db.UserBranchAssignments.AsNoTracking()
            .Where(x => x.TenantId == tenant && x.SubjectUserId == subject)
            .OrderByDescending(x => x.AssignmentType == BranchAssignmentType.Home).ThenBy(x => x.CreatedAt)
            .ToListAsync(ct);

    /// <summary>Resolve a caller's active branch for a (possibly absent) requested branch. Pure rules over the
    /// current rows — the caller decides the HTTP outcome from <see cref="BranchAssignmentRules.Resolution"/>.</summary>
    public async Task<BranchAssignmentRules.Resolution> ResolveAsync(string tenant, string subject, Guid? requested, CancellationToken ct = default)
    {
        var on = calendar.Today();   // 18.A3 — assignment validity is a Cairo date
        var rows = await db.UserBranchAssignments.AsNoTracking()
            .Where(x => x.TenantId == tenant && x.SubjectUserId == subject).ToListAsync(ct);
        return BranchAssignmentRules.ResolveActiveBranch(rows.Select(r => r.ToAssignment()), requested, on);
    }

    /// <summary>Switch the active branch: validate against the permitted set. Out-of-set ⇒ denied + audited
    /// BranchScopeDenied (High); in-set ⇒ ActiveBranchSwitched emitted (audited actor/from/to).</summary>
    public async Task<BranchAssignmentRules.Resolution> SwitchAsync(ActorContext actor, string tenant, string subject, Guid requested, CancellationToken ct = default)
    {
        var res = await ResolveAsync(tenant, subject, requested, ct);
        if (!res.Allowed)
        {
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "branch_scope", EntityId = requested.ToString(), Action = AuditAction.Decision,
                ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
                DecisionOutcome = "BranchScopeDenied", DecisionReasonCode = "branch-not-permitted", Severity = AuditSeverity.High,
            }, ct);
            return res;
        }

        var on = calendar.Today();   // 18.A3 — assignment validity is a Cairo date
        var rows = await db.UserBranchAssignments.AsNoTracking()
            .Where(x => x.TenantId == tenant && x.SubjectUserId == subject).ToListAsync(ct);
        var home = BranchAssignmentRules.HomeBranch(rows.Select(r => r.ToAssignment()), on);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "branch_scope", EntityId = requested.ToString(), Action = AuditAction.Decision,
            ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
            DecisionOutcome = "ActiveBranchSwitched", BeforeState = home?.ToString(), AfterState = requested.ToString(),
        }, ct);
        await outbox.EnqueueAsync("ActiveBranchSwitched", "admin.events",
            new { tenantId = tenant, subject, from = home, to = requested }, ct);
        return res;
    }

    private static AuditEventDraft Draft(UserBranchAssignment row, AuditAction action, ActorContext actor, string tenant,
        string? outcome = null, string? reason = null, AuditSeverity severity = AuditSeverity.Info) => new()
    {
        EntityType = "user_branch_assignment", EntityId = row.AssignmentId.ToString(), Action = action,
        ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
        DecisionOutcome = outcome, DecisionReasonCode = reason, Severity = severity,
    };
}
