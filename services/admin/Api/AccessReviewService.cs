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

    private async Task<bool> DecideAsync(ActorContext actor, string tenant, Guid itemId, ReviewDecision decision,
        string? note, bool revokeBinding, CancellationToken ct)
    {
        var item = await db.ReviewItems.FirstOrDefaultAsync(i => i.ItemId == itemId, ct);
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
