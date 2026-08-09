using System.Text.Json;
using Mersal.Admin.Domain;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Authz;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Api;

/// <summary>The acting admin (from the bearer token) — recorded on every audit event.</summary>
public sealed record ActorContext(string UserId, string Role, string? TenantId, bool Mfa);

/// <summary>Result of a grant attempt. On an SoD rejection <see cref="Violations"/> explains the conflict(s).
///
/// <para><see cref="Problem"/> carries a REFUSAL THE CALLER MUST SEE VERBATIM — currently only a breached
/// programme cap. It is not collapsed into <see cref="ReasonCode"/> because the numbers matter: "you are at your
/// limit" without the limit and the live count is not actionable, and an administrator needs to know whether to
/// free a slot or ask Mersal to raise the cap (design 40 §4).</para></summary>
public sealed record GrantResult(bool Ok, string? ReasonCode, RoleBinding? Binding,
    IReadOnlyList<SegregationOfDuties.Violation> Violations, Microsoft.AspNetCore.Http.IResult? Problem = null);

/// <summary>
/// User & role administration (phase 8b.1, FR-IAM-002/005/010). Assign / revoke role bindings and de-provision a
/// user across all portals — SoD-checked at grant time (<see cref="RoleAssignment"/> over the current active
/// bindings), justification-required, and immutably audited (grants, revocations, de-provision, AND the access-matrix
/// read). Bindings are soft-lifecycle: a revoke stamps metadata, never deletes.
/// </summary>
public sealed class RoleAdminService(AdminDbContext db, IAuditClient audit, TimeProvider clock, TenantProgramStore programs)
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

        // 21.4 — the CAPS, counted live inside the transaction that inserts (design 40 §4). The check and the
        // insert share one transaction because CheckLimitAsync takes a per-(tenant, limit) advisory lock scoped
        // to the CURRENT transaction: that is what stops two parallel grants at cap−1 each counting N−1 under
        // READ COMMITTED and both inserting. Outside a transaction the lock would be released immediately and
        // the serialization it exists for would silently not happen.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // A cap counts USERS, not bindings. Granting a second role to someone who already holds one consumes no
        // slot, so the check runs only when this grant would add a new distinct user — otherwise a tenant at its
        // cap could never adjust the roles of the people it already has, which is not what the cap means.
        var limited = await CheckUserCapsAsync(tenant, subject, scope, ct);
        if (limited is not null)
        {
            await tx.RollbackAsync(ct);
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "role_binding", EntityId = $"{subject}:{role}", Action = AuditAction.Grant,
                ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
                DecisionOutcome = "denied", DecisionReasonCode = ProgramEnablement.LimitReachedCode,
                Purpose = "role-assignment",
                // Notice, not Warning: this is a configured limit doing its job, not a suspicious act. But it is
                // audited, because "why could we not onboard anyone in March" is a real question later.
                Severity = AuditSeverity.Notice,
            }, ct);
            return new GrantResult(false, ProgramEnablement.LimitReachedCode, null, [], limited);
        }

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
        await tx.CommitAsync(ct);
        return new GrantResult(true, null, binding, []);
    }

    /// <summary>
    /// Whether this grant would breach either user cap, counted live.
    ///
    /// <para>The two caps ask different questions of the same grant. <c>active_users</c> grows only if the
    /// subject holds no active binding at all; <c>active_provider_users</c> grows only if they hold no active
    /// PROVIDER-scoped one — so a subject already active under a tenant-scoped role still consumes a provider
    /// slot the first time they are given a provider-scoped one. Getting this wrong in either direction is
    /// invisible: too strict and a tenant cannot re-role its existing staff, too loose and the cap does nothing.</para>
    ///
    /// <para>Counted with <c>count(DISTINCT subject_user_id)</c> against the real table inside the caller's
    /// transaction, which is what makes "revoke a binding and the slot frees immediately" true by construction
    /// rather than by remembering to decrement something.</para>
    /// </summary>
    private async Task<Microsoft.AspNetCore.Http.IResult?> CheckUserCapsAsync(
        string tenant, string subject, ScopeType scope, CancellationToken ct)
    {
        var activeBindings = await db.RoleBindings
            .Where(b => b.TenantId == tenant && b.SubjectUserId == subject && b.Status == BindingStatus.Active)
            .Select(b => b.ScopeType)
            .ToListAsync(ct);

        if (activeBindings.Count == 0)
        {
            var denied = await programs.CheckLimitAsync(
                tenant, ProgramLimits.ActiveUsers,
                token => db.Database.SqlQueryRaw<int>(
                    """
                    SELECT count(DISTINCT subject_user_id)::int AS "Value"
                    FROM admin.role_binding WHERE tenant_id = {0} AND status = 'Active'
                    """, tenant).SingleAsync(token),
                ct);
            if (denied is not null) return denied;
        }

        if (scope == ScopeType.Provider && !activeBindings.Contains(ScopeType.Provider))
        {
            var denied = await programs.CheckLimitAsync(
                tenant, ProgramLimits.ActiveProviderUsers,
                token => db.Database.SqlQueryRaw<int>(
                    """
                    SELECT count(DISTINCT subject_user_id)::int AS "Value"
                    FROM admin.role_binding
                    WHERE tenant_id = {0} AND status = 'Active' AND scope_type = 'Provider'
                    """, tenant).SingleAsync(token),
                ct);
            if (denied is not null) return denied;
        }

        return null;
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
