using System.Text.Json;
using Mersal.Admin.Domain;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Api;

/// <summary>
/// Periodic access-review campaigns (phase 8b.1, FR-IAM-007 / 19-audit-strategy §7). A campaign snapshots the
/// active T3/T4 grants into review items; a reviewer recertifies or revokes each (need-to-know); items left
/// unconfirmed at the deadline auto-expire — which revokes the underlying binding. Every review decision is audited
/// and linked to the grant.
/// </summary>
public sealed class AccessReviewService(AdminDbContext db, IAuditClient audit, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Open a campaign and snapshot every active binding at or above <paramref name="minTier"/> as a Pending item.</summary>
    public async Task<AccessReviewCampaign> CreateCampaignAsync(ActorContext actor, string tenant, string name,
        SensitivityTier minTier, DateTimeOffset dueAt, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var campaign = new AccessReviewCampaign
        {
            CampaignId = Guid.NewGuid(), TenantId = tenant, Name = name, MinTier = minTier,
            CreatedAt = now, CreatedBy = actor.UserId, DueAt = dueAt, Status = CampaignStatus.Open,
        };

        // Tiers at or above the campaign floor (compared as the mapped enum set — no int-cast on the text column).
        var inScopeTiers = Enum.GetValues<SensitivityTier>().Where(t => (int)t >= (int)minTier).ToList();
        var inScope = await db.RoleBindings
            .Where(b => b.TenantId == tenant && b.Status == BindingStatus.Active && inScopeTiers.Contains(b.Tier))
            .ToListAsync(ct);
        foreach (var b in inScope)
        {
            campaign.Items.Add(new AccessReviewItem
            {
                ItemId = Guid.NewGuid(), CampaignId = campaign.CampaignId, BindingId = b.BindingId,
                SubjectUserId = b.SubjectUserId, Role = b.Role, Decision = ReviewDecision.Pending,
            });
        }

        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(ct);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "access_review_campaign", EntityId = campaign.CampaignId.ToString(), Action = AuditAction.Create,
            ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
            AfterState = JsonSerializer.Serialize(new { name, minTier = minTier.ToString(), items = campaign.Items.Count }, Json),
            Purpose = "access-review", Severity = AuditSeverity.Notice,
        }, ct);
        return campaign;
    }

    /// <summary>Reviewer confirms continued need-to-know for a grant. Audited and linked to the binding.</summary>
    public async Task<bool> RecertifyAsync(ActorContext actor, string tenant, Guid itemId, string? note, CancellationToken ct = default)
        => await DecideAsync(actor, tenant, itemId, ReviewDecision.Recertified, note, revokeBinding: false, ct);

    /// <summary>Reviewer withdraws a grant — the review item is Revoked and the underlying binding is revoked.</summary>
    public async Task<bool> ReviewRevokeAsync(ActorContext actor, string tenant, Guid itemId, string? note, CancellationToken ct = default)
        => await DecideAsync(actor, tenant, itemId, ReviewDecision.Revoked, note, revokeBinding: true, ct);

    /// <summary>
    /// The items a campaign is reviewing, oldest decision last — the reviewer's actual worklist.
    /// </summary>
    /// <remarks>
    /// Scoped through the CAMPAIGN, which is the only thing here that carries a tenant. See
    /// <see cref="DecideAsync"/> for why that matters: <c>access_review_item</c> is deliberately not
    /// tenant-isolated, on the stated ground that its rows are "reached only through their campaign", and a
    /// query that does not join the campaign quietly makes that untrue.
    /// </remarks>
    public async Task<IReadOnlyList<AccessReviewItemView>> ItemsAsync(
        string tenant, Guid campaignId, CancellationToken ct = default)
    {
        var rows = await db.ReviewItems.AsNoTracking()
            .Where(i => i.CampaignId == campaignId
                        && db.Campaigns.Any(c => c.CampaignId == i.CampaignId && c.TenantId == tenant))
            // Pending first — this list is opened to do the outstanding work, not to browse decisions already
            // taken. Within each band, stable by role then subject so a reviewer working down it twice sees
            // the same order.
            .OrderBy(i => i.Decision == ReviewDecision.Pending ? 0 : 1)
            .ThenBy(i => i.Role).ThenBy(i => i.SubjectUserId)
            .ToListAsync(ct);

        return [.. rows.Select(i => new AccessReviewItemView(
            i.ItemId, i.BindingId, i.SubjectUserId, i.Role, i.Decision.ToString(),
            i.DecidedBy, i.DecidedAt, i.Note))];
    }

    private async Task<bool> DecideAsync(ActorContext actor, string tenant, Guid itemId, ReviewDecision decision,
        string? note, bool revokeBinding, CancellationToken ct)
    {
        /*
         * SCOPED THROUGH THE CAMPAIGN — 33.7.
         *
         * This read used to be `FirstOrDefaultAsync(i => i.ItemId == itemId)`, with `tenant` used only to
         * stamp the audit event. `admin.access_review_item` carries no `tenant_id` and has no RLS policy;
         * migration 0005 lists it under "deliberately NOT tenant-isolated" with the reason "child rows
         * reached only through their campaign, which IS isolated". This query did not reach it through the
         * campaign, so that reason did not hold here.
         *
         * The underlying binding was safe either way — `role_binding` IS isolated, so RevokeUnderlyingAsync
         * would find nothing and return. The REVIEW RECORD was not: another tenant's administrator, holding
         * an item id, could mark that item Recertified. Recertifying is precisely what stops SweepExpiredAsync
         * from revoking a grant at the deadline, so the reachable act was not "read something they shouldn't"
         * but "silently defeat another tenant's access review of its own T4 grants".
         *
         * The `db.Campaigns` subquery passes through the campaign's own RLS policy as well as this explicit
         * predicate, so the two controls have to fail together.
         */
        var item = await db.ReviewItems.FirstOrDefaultAsync(
            i => i.ItemId == itemId
                 && db.Campaigns.Any(c => c.CampaignId == i.CampaignId && c.TenantId == tenant), ct);
        if (item is null || item.Decision != ReviewDecision.Pending) return false;

        item.Decision = decision;
        item.DecidedBy = actor.UserId;
        item.DecidedAt = clock.GetUtcNow();
        item.Note = note;

        if (revokeBinding) await RevokeUnderlyingAsync(item, actor.UserId, "access-review revoke", ct);
        await db.SaveChangesAsync(ct);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "access_review_item", EntityId = item.ItemId.ToString(), Action = AuditAction.Decision,
            ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
            DecisionOutcome = decision.ToString(),
            AfterState = JsonSerializer.Serialize(new { item.BindingId, item.SubjectUserId, item.Role, note }, Json),
            Purpose = "access-review", Severity = AuditSeverity.Notice,
        }, ct);
        return true;
    }

    /// <summary>Auto-expire every item still Pending past the campaign deadline: the grant is revoked (stale access
    /// removed) and the decision audited. Closes the campaign. Returns the number of grants auto-expired.</summary>
    public async Task<int> SweepExpiredAsync(ActorContext actor, string tenant, Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await db.Campaigns.Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.CampaignId == campaignId && c.TenantId == tenant, ct);
        if (campaign is null) return 0;

        var now = clock.GetUtcNow();
        if (now < campaign.DueAt) return 0; // not yet due

        var expired = 0;
        foreach (var item in campaign.Items.Where(i => i.Decision == ReviewDecision.Pending))
        {
            item.Decision = ReviewDecision.AutoExpired;
            item.DecidedAt = now;
            item.Note = "auto-expired: not recertified by deadline";
            await RevokeUnderlyingAsync(item, actor.UserId, "access-review auto-expiry", ct);
            expired++;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "access_review_item", EntityId = item.ItemId.ToString(), Action = AuditAction.Decision,
                ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
                DecisionOutcome = "AutoExpired",
                AfterState = JsonSerializer.Serialize(new { item.BindingId, item.SubjectUserId, item.Role }, Json),
                Purpose = "access-review", Severity = AuditSeverity.Warning,
            }, ct);
        }

        campaign.Status = CampaignStatus.Closed;
        await db.SaveChangesAsync(ct);
        return expired;
    }

    private async Task RevokeUnderlyingAsync(AccessReviewItem item, string by, string reason, CancellationToken ct)
    {
        var binding = await db.RoleBindings
            .FirstOrDefaultAsync(b => b.BindingId == item.BindingId && b.Status == BindingStatus.Active, ct);
        if (binding is null) return;
        binding.Status = BindingStatus.Revoked;
        binding.RevokedAt = clock.GetUtcNow();
        binding.RevokedBy = by;
        binding.RevokeReason = reason;
    }
}
