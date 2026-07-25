using Mersal.Authz;

namespace Mersal.Admin.Api;

/// <summary>Access-review console endpoints (phase 8b.1, FR-IAM-007): open a recertification campaign, recertify or
/// revoke each grant, and sweep unconfirmed grants to auto-expiry after the deadline.</summary>
public static class AccessReviewEndpoints
{
    public static void MapAccessReview(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/admin/access-reviews").WithTags("admin-access-review");

        g.MapPost("/", async (CreateCampaignRequest req, AdminGate gate, AccessReviewService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.Review, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var tenant = AdminContracts.ResolveTenant(p, req.Tenant);
            if (tenant is null) return Results.BadRequest(new { error = "no-tenant" });

            var c = await svc.CreateCampaignAsync(AdminContracts.Actor(p), tenant, req.Name, req.Tier, req.DueAt, ct);
            return Results.Created($"/api/v1/admin/access-reviews/{c.CampaignId}",
                new { c.CampaignId, c.Name, minTier = c.MinTier.ToString(), items = c.Items.Count, c.DueAt });
        });

        g.MapPost("/items/{itemId:guid}/recertify", async (Guid itemId, ReviewDecisionRequest req, AdminGate gate, AccessReviewService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.Review, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var tenant = AdminContracts.ResolveTenant(p, req.Tenant);
            if (tenant is null) return Results.BadRequest(new { error = "no-tenant" });

            var ok = await svc.RecertifyAsync(AdminContracts.Actor(p), tenant, itemId, req.Note, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        g.MapPost("/items/{itemId:guid}/revoke", async (Guid itemId, ReviewDecisionRequest req, AdminGate gate, AccessReviewService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.Review, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var tenant = AdminContracts.ResolveTenant(p, req.Tenant);
            if (tenant is null) return Results.BadRequest(new { error = "no-tenant" });

            var ok = await svc.ReviewRevokeAsync(AdminContracts.Actor(p), tenant, itemId, req.Note, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        // Sweep the campaign: any grant still unconfirmed past the deadline auto-expires (revoked).
        g.MapPost("/{campaignId:guid}/sweep", async (Guid campaignId, string? tenant, AdminGate gate, AccessReviewService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.Review, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var t = AdminContracts.ResolveTenant(p, tenant);
            if (t is null) return Results.BadRequest(new { error = "no-tenant" });

            var expired = await svc.SweepExpiredAsync(AdminContracts.Actor(p), t, campaignId, ct);
            return Results.Ok(new { campaignId, autoExpired = expired });
        });
    }
}
