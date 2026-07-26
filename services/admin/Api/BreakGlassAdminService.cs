using System.Text.Json;
using Mersal.Admin.Domain;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Api;

/// <summary>Outcome of a break-glass lifecycle step.</summary>
public sealed record BreakGlassResult(bool Ok, string? ReasonCode, BreakGlassGrantRecord? Grant);

/// <summary>An active grant as the runtime break-glass provider consumes it (16.6, H5) — window + scope only, no
/// requester justification (min-necessary; the reading service just needs to know what is widened, and until when).</summary>
public sealed record ActiveGrantView(Guid GrantId, DateTimeOffset NotBefore, DateTimeOffset ExpiresAt,
    IReadOnlyList<string> ScopedResourceTypes, IReadOnlyList<string> ScopedResourceIds);

/// <summary>
/// Break-glass administration (phase 8b.3, FR-IAM-009 / 18-security-model §11). The full flow: request (reason
/// code + justification, scoped resources) → dual-control approval (approver ≠ requester, SoD-enforced) → step-up
/// MFA to activate → scoped, auto-expiring window → loud high-severity audit on every access → auto-expiry. It
/// never enables self-approval nor widens access beyond its explicit scope (no field-deny bypass). An active grant
/// maps to the runtime <c>libs/authz/BreakGlassGrant</c> a downstream service's engine consults (live cross-service
/// wiring deferred to the shared bus, same seam as phases 5–8).
/// </summary>
public sealed class BreakGlassAdminService(AdminDbContext db, IAuditClient audit, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BreakGlassGrantRecord> RequestAsync(ActorContext actor, string tenant, string reasonCode,
        string justification, IReadOnlyList<string> scopedTypes, IReadOnlyList<string> scopedIds, int windowMinutes, CancellationToken ct = default)
    {
        var grant = new BreakGlassGrantRecord
        {
            GrantId = Guid.NewGuid(), TenantId = tenant, RequesterUserId = actor.UserId,
            ReasonCode = reasonCode, Justification = justification,
            ScopedResourceTypesJson = JsonSerializer.Serialize(scopedTypes, Json),
            ScopedResourceIdsJson = JsonSerializer.Serialize(scopedIds, Json),
            WindowMinutes = windowMinutes, Status = BreakGlassStatus.Requested, RequestedAt = clock.GetUtcNow(),
        };
        db.BreakGlassGrants.Add(grant);
        await db.SaveChangesAsync(ct);

        await Emit(grant, actor, AuditAction.Grant, "requested", AuditSeverity.High,
            new { reasonCode, scopedTypes, windowMinutes }, ct);
        return grant;
    }

    /// <summary>Dual-control approval. A self-approval (approver == requester) is REJECTED and audited high-severity.</summary>
    public async Task<BreakGlassResult> ApproveAsync(ActorContext actor, string tenant, Guid grantId, CancellationToken ct = default)
    {
        var grant = await Load(tenant, grantId, ct);
        if (grant is null || grant.Status != BreakGlassStatus.Requested) return new(false, "not-approvable", grant);

        if (!BreakGlassPolicy.CanApprove(grant, actor.UserId))
        {
            await Emit(grant, actor, AuditAction.Decision, "self-approval-denied", AuditSeverity.High, null, ct);
            return new(false, "self-approval-denied", grant);   // dual control / no SoD bypass
        }

        grant.Status = BreakGlassStatus.Approved;
        grant.ApproverUserId = actor.UserId;
        grant.ApprovedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        await Emit(grant, actor, AuditAction.Decision, "approved", AuditSeverity.High, null, ct);
        return new(true, null, grant);
    }

    public async Task<BreakGlassResult> RejectAsync(ActorContext actor, string tenant, Guid grantId, string reason, CancellationToken ct = default)
    {
        var grant = await Load(tenant, grantId, ct);
        if (grant is null || grant.Status != BreakGlassStatus.Requested) return new(false, "not-approvable", grant);
        grant.Status = BreakGlassStatus.Rejected;
        grant.RejectReason = reason;
        await db.SaveChangesAsync(ct);
        await Emit(grant, actor, AuditAction.Decision, "rejected", AuditSeverity.Notice, new { reason }, ct);
        return new(true, null, grant);
    }

    /// <summary>Activate an approved grant — requires the requester + a satisfied step-up MFA. Opens the scoped,
    /// auto-expiring window.</summary>
    public async Task<BreakGlassResult> ActivateAsync(ActorContext actor, string tenant, Guid grantId, bool stepUpSatisfied, CancellationToken ct = default)
    {
        var grant = await Load(tenant, grantId, ct);
        if (grant is null || grant.Status != BreakGlassStatus.Approved) return new(false, "not-activatable", grant);
        if (!string.Equals(grant.RequesterUserId, actor.UserId, StringComparison.Ordinal)) return new(false, "not-requester", grant);
        if (!stepUpSatisfied) return new(false, "step-up-required", grant);

        var now = clock.GetUtcNow();
        var (notBefore, expiresAt) = BreakGlassPolicy.Window(now, grant.WindowMinutes);
        grant.Status = BreakGlassStatus.Active;
        grant.StepUpSatisfied = true;
        grant.ActivatedAt = now;
        grant.NotBefore = notBefore;
        grant.ExpiresAt = expiresAt;
        await db.SaveChangesAsync(ct);

        await Emit(grant, actor, AuditAction.StateChange, "activated", AuditSeverity.High, new { expiresAt }, ct);
        return new(true, null, grant);
    }

    /// <summary>Record an access under a grant. Returns true only if the grant is active AND the resource is in
    /// scope — an out-of-scope access is logged (within_scope = false) and denied (no field-deny bypass). Every
    /// access emits a HIGH-severity break_glass audit event (loud audit + Security/DPO alert seam).</summary>
    public async Task<bool> RecordAccessAsync(ActorContext actor, string tenant, Guid grantId, string resourceType,
        string? resourceId, string action, CancellationToken ct = default)
    {
        var grant = await Load(tenant, grantId, ct);
        var now = clock.GetUtcNow();
        var active = grant is not null && grant.IsActiveAt(now);
        var inScope = active && BreakGlassPolicy.InScope(
            Deserialize(grant!.ScopedResourceTypesJson), Deserialize(grant.ScopedResourceIdsJson), resourceType, resourceId);

        if (grant is not null)
        {
            db.BreakGlassAccesses.Add(new BreakGlassAccess
            {
                AccessId = Guid.NewGuid(), GrantId = grant.GrantId, TenantId = tenant, ActorUserId = actor.UserId,
                ResourceType = resourceType, ResourceId = resourceId, Action = action, WithinScope = inScope, AccessedAt = now,
            });
            await db.SaveChangesAsync(ct);
        }

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "break_glass_access", EntityId = grantId.ToString(), Action = AuditAction.Read,
            ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
            DecisionOutcome = inScope ? "granted" : "denied-out-of-scope",
            AfterState = JsonSerializer.Serialize(new { resourceType, resourceId, action, inScope }, Json),
            Purpose = "break-glass", BreakGlass = true, Severity = AuditSeverity.High,
        }, ct);
        return inScope;
    }

    /// <summary>Expire every Active grant past its window. Returns the number expired.</summary>
    public async Task<int> SweepExpiredAsync(string tenant, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var due = await db.BreakGlassGrants
            .Where(g => g.TenantId == tenant && g.Status == BreakGlassStatus.Active && g.ExpiresAt != null && g.ExpiresAt <= now)
            .ToListAsync(ct);
        foreach (var g in due) g.Status = BreakGlassStatus.Expired;
        if (due.Count > 0) await db.SaveChangesAsync(ct);
        return due.Count;
    }

    /// <summary>16.6 (H5): the currently-active grants for a subject in a tenant — the runtime break-glass
    /// provider in every service reads this (caller's own token) to decide whether to widen access now.</summary>
    public async Task<IReadOnlyList<ActiveGrantView>> ActiveForSubjectAsync(string subject, string tenant, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var grants = await db.BreakGlassGrants.AsNoTracking()
            .Where(g => g.TenantId == tenant && g.RequesterUserId == subject && g.Status == BreakGlassStatus.Active
                        && g.NotBefore != null && g.NotBefore <= now && g.ExpiresAt != null && g.ExpiresAt > now)
            .ToListAsync(ct);
        return grants.Select(g => new ActiveGrantView(
            g.GrantId, g.NotBefore!.Value, g.ExpiresAt!.Value,
            Deserialize(g.ScopedResourceTypesJson), Deserialize(g.ScopedResourceIdsJson))).ToList();
    }

    private Task<BreakGlassGrantRecord?> Load(string tenant, Guid grantId, CancellationToken ct) =>
        db.BreakGlassGrants.FirstOrDefaultAsync(g => g.GrantId == grantId && g.TenantId == tenant, ct);

    private static IReadOnlyList<string> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private async Task Emit(BreakGlassGrantRecord g, ActorContext actor, AuditAction action, string outcome,
        AuditSeverity severity, object? after, CancellationToken ct)
    {
        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "break_glass_grant", EntityId = g.GrantId.ToString(), Action = action,
            ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = g.TenantId, ActorMfa = actor.Mfa,
            DecisionOutcome = outcome, Purpose = "break-glass", BreakGlass = true, Severity = severity,
            AfterState = after is null ? null : JsonSerializer.Serialize(after, Json),
        }, ct);
    }
}
