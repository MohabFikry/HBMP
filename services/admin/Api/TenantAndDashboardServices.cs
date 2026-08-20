using System.Text.Json;
using Mersal.Admin.Domain;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Authz;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Api;

/// <summary>Tenant administration (phase 8b.3, FR-IAM-008) — Super Admin manages platform tenants. Every domain row
/// carries tenant_id; RLS prevents cross-tenant leakage. Audited.</summary>
public sealed class TenantAdminService(AdminDbContext db, IAuditClient audit, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Tenant> UpsertAsync(ActorContext actor, string tenantId, string name, bool active, CancellationToken ct = default)
    {
        var existing = await db.Tenants.FirstOrDefaultAsync(t => t.TenantId == tenantId, ct);
        if (existing is null)
        {
            existing = new Tenant { TenantId = tenantId, Name = name, Active = active, CreatedBy = actor.UserId, CreatedAt = clock.GetUtcNow() };
            db.Tenants.Add(existing);
        }
        else { existing.Name = name; existing.Active = active; }
        await db.SaveChangesAsync(ct);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "tenant", EntityId = tenantId, Action = AuditAction.Update,
            ActorUserId = actor.UserId, ActorRole = actor.Role, ActorMfa = actor.Mfa,
            AfterState = JsonSerializer.Serialize(new { tenantId, name, active }, Json),
            Purpose = "tenant-administration", Severity = AuditSeverity.Notice,
        }, ct);
        return existing;
    }

    public Task<List<Tenant>> ListAsync(CancellationToken ct = default) =>
        db.Tenants.AsNoTracking().OrderBy(t => t.TenantId).ToListAsync(ct);
}

/// <summary>The break-glass dashboard rows (grants + their access counts + review status).</summary>
public sealed record BreakGlassDashboardRow(Guid GrantId, string Requester, string? Approver, string Status,
    string ReasonCode, int AccessCount, int OutOfScopeCount, DateTimeOffset? ExpiresAt, bool PostReviewDone);

/// <summary>A latent SoD conflict detected among a user's currently-held active bindings.</summary>
public sealed record SodViolationRow(string SubjectUserId, string HeldRole, string ConflictingRole, string Reason);

/// <summary>
/// Read-only audit / access-review / break-glass dashboards (phase 8b.3, 19-audit-strategy §7). Tenant-scoped: a
/// tenant admin sees only their tenant; Super Admin passes the tenant it is inspecting (or a global roll-up is a
/// per-tenant call). Viewing a dashboard is itself an audited read. The break-glass + access-review data is owned by
/// admin-service; the broader hash-chained audit feed lives in audit-service (queried there).
/// </summary>
public sealed class DashboardService(AdminDbContext db, IAuditClient audit)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Break-glass grants for a tenant with their access counts — for post-hoc review.</summary>
    public async Task<IReadOnlyList<BreakGlassDashboardRow>> BreakGlassAsync(ActorContext actor, string tenant, CancellationToken ct = default)
    {
        var grants = await db.BreakGlassGrants.AsNoTracking().Where(g => g.TenantId == tenant).ToListAsync(ct);
        var accesses = await db.BreakGlassAccesses.AsNoTracking().Where(a => a.TenantId == tenant).ToListAsync(ct);
        var byGrant = accesses.GroupBy(a => a.GrantId).ToDictionary(g => g.Key, g => g.ToList());

        var rows = grants.Select(g =>
        {
            var acc = byGrant.TryGetValue(g.GrantId, out var l) ? l : [];
            return new BreakGlassDashboardRow(g.GrantId, g.RequesterUserId, g.ApproverUserId, g.Status.ToString(),
                g.ReasonCode, acc.Count, acc.Count(a => !a.WithinScope), g.ExpiresAt, g.PostReviewDone);
        }).ToList();

        await AuditView(actor, tenant, "break-glass", rows.Count, ct);
        return rows;
    }

    /// <summary>Access-review campaign status roll-up (open/closed + item decision counts).</summary>
    public async Task<IReadOnlyList<AccessReviewCampaignView>> AccessReviewAsync(
        ActorContext actor, string tenant, CancellationToken ct = default)
    {
        var campaigns = await db.Campaigns.AsNoTracking().Include(c => c.Items)
            .Where(c => c.TenantId == tenant).ToListAsync(ct);
        // 31.6 — `Task<object>` was the last thing on this service with no describable response. The shape was
        // never in doubt; it simply had no name, so the spec could not carry it.
        var view = campaigns.Select(c => new AccessReviewCampaignView(
            c.CampaignId, c.Name, c.Status.ToString(), c.DueAt,
            c.Items.Count,
            c.Items.Count(i => i.Decision == ReviewDecision.Pending),
            c.Items.Count(i => i.Decision == ReviewDecision.Recertified),
            c.Items.Count(i => i.Decision == ReviewDecision.Revoked),
            c.Items.Count(i => i.Decision == ReviewDecision.AutoExpired))).ToList();

        await AuditView(actor, tenant, "access-review", view.Count, ct);
        return view;
    }

    /// <summary>Latent SoD violations across a tenant's active bindings (defense-in-depth — flags any conflict a
    /// grant path might have missed). Surfaced high-severity to reviewers.</summary>
    public async Task<IReadOnlyList<SodViolationRow>> SodViolationsAsync(ActorContext actor, string tenant, CancellationToken ct = default)
    {
        var bindings = await db.RoleBindings.AsNoTracking()
            .Where(b => b.TenantId == tenant && b.Status == BindingStatus.Active).ToListAsync(ct);
        var byUser = bindings.GroupBy(b => b.SubjectUserId);

        var rows = new List<SodViolationRow>();
        foreach (var user in byUser)
        {
            var roles = user.Select(b => b.Role).ToList();
            // Evaluate each role against the others held by the same user.
            for (var i = 0; i < roles.Count; i++)
            {
                var held = roles.Where((_, idx) => idx != i);
                foreach (var v in SegregationOfDuties.Evaluate(held, [roles[i]]))
                    rows.Add(new SodViolationRow(user.Key, v.HeldToken, v.ConflictingToken, v.Reason));
            }
        }
        var distinct = rows.DistinctBy(r => (r.SubjectUserId, r.HeldRole, r.ConflictingRole)).ToList();

        await AuditView(actor, tenant, "sod-violations", distinct.Count, ct);
        return distinct;
    }

    private async Task AuditView(ActorContext actor, string tenant, string dashboard, int rowCount, CancellationToken ct) =>
        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "dashboard", EntityId = dashboard, Action = AuditAction.Read,
            ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
            AfterState = JsonSerializer.Serialize(new { dashboard, tenant, rowCount }, Json),
            Purpose = "governance-dashboard", Severity = AuditSeverity.Notice,
        }, ct);
}
