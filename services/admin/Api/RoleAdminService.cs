using System.Text.Json;
using Mersal.Admin.Domain;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Authz;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Api;

/// <summary>The acting admin (from the bearer token) — recorded on every audit event.</summary>
public sealed record ActorContext(string UserId, string Role, string? TenantId, bool Mfa);

/// <summary>Result of a grant attempt. On an SoD rejection <see cref="Violations"/> explains the conflict(s).</summary>
public sealed record GrantResult(bool Ok, string? ReasonCode, RoleBinding? Binding,
    IReadOnlyList<SegregationOfDuties.Violation> Violations);

/// <summary>
/// User & role administration (phase 8b.1, FR-IAM-002/005/010). Assign / revoke role bindings and de-provision a
/// user across all portals — SoD-checked at grant time (<see cref="RoleAssignment"/> over the current active
/// bindings), justification-required, and immutably audited (grants, revocations, de-provision, AND the access-matrix
/// read). Bindings are soft-lifecycle: a revoke stamps metadata, never deletes.
/// </summary>
public sealed class RoleAdminService(AdminDbContext db, IAuditClient audit, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The roles a user actively holds in a tenant (empty if de-provisioned — immediate revocation everywhere).</summary>
    public async Task<IReadOnlyList<string>> EffectiveRolesAsync(string tenant, string subject, CancellationToken ct = default)
    {
        if (await db.DeprovisionedUsers.AnyAsync(d => d.TenantId == tenant && d.SubjectUserId == subject, ct))
            return [];
        return await db.RoleBindings
            .Where(b => b.TenantId == tenant && b.SubjectUserId == subject && b.Status == BindingStatus.Active)
            .Select(b => b.Role).ToListAsync(ct);
    }

    public async Task<GrantResult> GrantAsync(ActorContext actor, string tenant, string subject, string role,
        ScopeType scope, string? providerId, string justification, CancellationToken ct = default)
    {
        role = role.Trim().ToLowerInvariant();
        var held = await db.RoleBindings
            .Where(b => b.TenantId == tenant && b.SubjectUserId == subject && b.Status == BindingStatus.Active)
            .Select(b => b.Role).ToListAsync(ct);

        var eval = RoleAssignment.Evaluate(held, role);
        if (!eval.Allowed)
        {
            // A rejected grant (esp. an SoD conflict or self-elevation) is a high-severity audit event.
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "role_binding", EntityId = $"{subject}:{role}", Action = AuditAction.Grant,
                ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
                DecisionOutcome = "denied", DecisionReasonCode = eval.ReasonCode,
                AfterState = eval.Violations.Count > 0 ? JsonSerializer.Serialize(eval.Violations, Json) : null,
                Purpose = "role-assignment",
                Severity = eval.ReasonCode == "sod-conflict" ? AuditSeverity.High : AuditSeverity.Warning,
            }, ct);
            return new GrantResult(false, eval.ReasonCode, null, eval.Violations);
        }

        var now = clock.GetUtcNow();
        var tier = RoleCatalog.TierOf(role);
        var binding = new RoleBinding
        {
            BindingId = Guid.NewGuid(), TenantId = tenant, SubjectUserId = subject, Role = role,
            ScopeType = scope, ProviderId = providerId, Tier = tier,
            GrantedBy = actor.UserId, Justification = justification, GrantedAt = now,
            ReviewDueAt = RoleAssignment.ReviewDueAt(tier, now), Status = BindingStatus.Active,
        };
        db.RoleBindings.Add(binding);
        await db.SaveChangesAsync(ct);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "role_binding", EntityId = binding.BindingId.ToString(), Action = AuditAction.Grant,
            ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
            DecisionOutcome = "granted",
            AfterState = JsonSerializer.Serialize(new { subject, role, scope = scope.ToString(), tier = tier.ToString(), justification }, Json),
            Purpose = "role-assignment", Severity = AuditSeverity.Notice,
        }, ct);
        return new GrantResult(true, null, binding, []);
    }

    /// <summary>Revoke a single active binding (audited). Returns false if no such active binding.</summary>
    public async Task<bool> RevokeAsync(ActorContext actor, string tenant, Guid bindingId, string reason, CancellationToken ct = default)
    {
        var binding = await db.RoleBindings
            .FirstOrDefaultAsync(b => b.BindingId == bindingId && b.TenantId == tenant && b.Status == BindingStatus.Active, ct);
        if (binding is null) return false;

        RevokeBinding(binding, actor.UserId, reason);
        await db.SaveChangesAsync(ct);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "role_binding", EntityId = binding.BindingId.ToString(), Action = AuditAction.SoftDelete,
            ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
            BeforeState = JsonSerializer.Serialize(new { binding.SubjectUserId, binding.Role, status = "Active" }, Json),
            AfterState = JsonSerializer.Serialize(new { status = "Revoked", reason }, Json),
            Purpose = "role-revocation", Severity = AuditSeverity.Notice,
        }, ct);
        return true;
    }

    /// <summary>De-provision a user (FR-IAM-010): revoke EVERY active binding and record the block so any portal/API
    /// denies the subject immediately. Audited as a high-severity state change.</summary>
    public async Task DeprovisionAsync(ActorContext actor, string tenant, string subject, string reason, CancellationToken ct = default)
    {
        var active = await db.RoleBindings
            .Where(b => b.TenantId == tenant && b.SubjectUserId == subject && b.Status == BindingStatus.Active)
            .ToListAsync(ct);
        foreach (var b in active) RevokeBinding(b, actor.UserId, "de-provisioned: " + reason);

        if (!await db.DeprovisionedUsers.AnyAsync(d => d.TenantId == tenant && d.SubjectUserId == subject, ct))
        {
            db.DeprovisionedUsers.Add(new DeprovisionedUser
            {
                Id = Guid.NewGuid(), TenantId = tenant, SubjectUserId = subject,
                DeprovisionedBy = actor.UserId, Reason = reason, DeprovisionedAt = clock.GetUtcNow(),
            });
        }
        await db.SaveChangesAsync(ct);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "user", EntityId = subject, Action = AuditAction.StateChange,
            ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
            AfterState = JsonSerializer.Serialize(new { subject, deprovisioned = true, revokedBindings = active.Count, reason }, Json),
            Purpose = "de-provisioning", Severity = AuditSeverity.High,
        }, ct);
    }

    /// <summary>Read the access matrix (active bindings) for a tenant — an audited admin READ (who saw the matrix).</summary>
    public async Task<IReadOnlyList<RoleBinding>> ReadAccessMatrixAsync(ActorContext actor, string tenant, CancellationToken ct = default)
    {
        var rows = await db.RoleBindings.AsNoTracking()
            .Where(b => b.TenantId == tenant && b.Status == BindingStatus.Active)
            .OrderBy(b => b.SubjectUserId).ToListAsync(ct);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "access_matrix", EntityId = tenant, Action = AuditAction.Read,
            ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
            AfterState = JsonSerializer.Serialize(new { rowCount = rows.Count }, Json),
            Purpose = "access-review", Severity = AuditSeverity.Notice,
        }, ct);
        return rows;
    }

    private void RevokeBinding(RoleBinding binding, string by, string reason)
    {
        binding.Status = BindingStatus.Revoked;
        binding.RevokedAt = clock.GetUtcNow();
        binding.RevokedBy = by;
        binding.RevokeReason = reason;
    }
}
