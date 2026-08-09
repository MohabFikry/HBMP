using Mersal.Auth.Authorization;
using Mersal.Authz;

namespace Mersal.Admin.Api;

/// <summary>Access-review console endpoints (phase 8b.1, FR-IAM-007): open a recertification campaign, recertify or
/// revoke each grant, and sweep unconfirmed grants to auto-expiry after the deadline.</summary>
public static class AccessReviewEndpoints
{
    public static void MapAccessReview(this WebApplication app)
    {
        // 18.B3 (audit R2 S3) — the framework gate. Until now these groups carried NO .RequireAuthorization,
        // so an UNAUTHENTICATED request reached the handler and was rejected only by AdminGate's in-handler
        // check. That worked, but it made the whole surface depend on every handler remembering to call the
        // gate first, and it never enforced MFA at the pipeline. Group scope = admin:read (authn + admin-ness +
        // MFA); mutations add admin:write on top; AdminGate stays as layer two for the per-action rule + audit.
        var g = app.MapGroup("/api/v1/admin/access-reviews").WithTags("admin-access-review").RequireAuthorization(HbmpPolicies.Scope("admin:read"));
        var w = g.MapGroup("").RequireAuthorization(HbmpPolicies.Scope("admin:write"));

        w.MapPost("/", async (CreateCampaignRequest req, AdminGate gate, AccessReviewService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.Review, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var scope = gate.BindTenant(req.Tenant);
            if (!scope.IsAllowed) return scope.ToProblem();
            var tenant = scope.Tenant!;

            var c = await svc.CreateCampaignAsync(AdminContracts.Actor(p), tenant, req.Name, req.Tier, req.DueAt, ct);
            return Results.Created($"/api/v1/admin/access-reviews/{c.CampaignId}",
                new { c.CampaignId, c.Name, minTier = c.MinTier.ToString(), items = c.Items.Count, c.DueAt });
        });

        w.MapPost("/items/{itemId:guid}/recertify", async (Guid itemId, ReviewDecisionRequest req, AdminGate gate, AccessReviewService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.Review, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var scope = gate.BindTenant(req.Tenant);
            if (!scope.IsAllowed) return scope.ToProblem();
            var tenant = scope.Tenant!;

            var ok = await svc.RecertifyAsync(AdminContracts.Actor(p), tenant, itemId, req.Note, ct);
            return ok ? Results.NoContent() : Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
        });

        w.MapPost("/items/{itemId:guid}/revoke", async (Guid itemId, ReviewDecisionRequest req, AdminGate gate, AccessReviewService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.Review, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var scope = gate.BindTenant(req.Tenant);
            if (!scope.IsAllowed) return scope.ToProblem();
            var tenant = scope.Tenant!;

            var ok = await svc.ReviewRevokeAsync(AdminContracts.Actor(p), tenant, itemId, req.Note, ct);
            return ok ? Results.NoContent() : Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
        });

        // Sweep the campaign: any grant still unconfirmed past the deadline auto-expires (revoked).
        w.MapPost("/{campaignId:guid}/sweep", async (Guid campaignId, string? tenant, AdminGate gate, AccessReviewService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.Review, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var scope = gate.BindTenant(tenant);
            if (!scope.IsAllowed) return scope.ToProblem();
            var t = scope.Tenant!;

            var expired = await svc.SweepExpiredAsync(AdminContracts.Actor(p), t, campaignId, ct);
            return Results.Ok(new AccessReviewSweepView(campaignId, expired));
        })
        .Produces<AccessReviewSweepView>();
    }
}
