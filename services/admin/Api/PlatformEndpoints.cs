using Mersal.Authz;

namespace Mersal.Admin.Api;

public sealed record TenantUpsertRequest(string TenantId, string Name, bool Active = true);
public sealed record BreakGlassRequestBody(string ReasonCode, string Justification,
    IReadOnlyList<string> ScopedResourceTypes, IReadOnlyList<string>? ScopedResourceIds = null,
    int WindowMinutes = 60, string? Tenant = null);
public sealed record BreakGlassRejectBody(string Reason, string? Tenant = null);
public sealed record BreakGlassActivateBody(bool StepUpSatisfied, string? Tenant = null);
public sealed record BreakGlassAccessBody(string ResourceType, string? ResourceId, string Action, string? Tenant = null);

/// <summary>Tenant administration, break-glass lifecycle, and governance-dashboard endpoints (phase 8b.3).</summary>
public static class PlatformEndpoints
{
    public static void MapPlatform(this WebApplication app)
    {
        // -------------------------------------------------- Tenant administration (Super Admin)
        var tenants = app.MapGroup("/api/v1/admin/tenants").WithTags("admin-tenants");
        tenants.MapPut("/", async (TenantUpsertRequest req, AdminGate gate, TenantAdminService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.ManageTenant, ct);
            if (denied is not null) return denied;
            var t = await svc.UpsertAsync(AdminContracts.Actor(gate.Principal!), req.TenantId, req.Name, req.Active, ct);
            return Results.Ok(new { t.TenantId, t.Name, t.Active });
        });
        tenants.MapGet("/", async (AdminGate gate, TenantAdminService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.ManageTenant, ct);
            if (denied is not null) return denied;
            return Results.Ok(await svc.ListAsync(ct));
        });

        // -------------------------------------------------- Break-glass lifecycle
        var bg = app.MapGroup("/api/v1/admin/break-glass").WithTags("admin-break-glass");

        // 16.6 (H5): the runtime seam — every service's break-glass provider reads the CALLER's own active grants
        // here (caller's token forwarded) to widen access at decision time. Self-scoped (subject from the token),
        // so it needs authentication only, not the admin role; min-necessary (window + scope, no justification).
        bg.MapGet("/active", async (Mersal.Auth.IHbmpPrincipalAccessor me, BreakGlassAdminService svc, CancellationToken ct) =>
        {
            var p = me.Principal;
            if (p is null) return Results.Unauthorized();
            if (string.IsNullOrEmpty(p.TenantId)) return Results.Ok(Array.Empty<ActiveGrantView>());
            return Results.Ok(await svc.ActiveForSubjectAsync(p.Subject, p.TenantId, ct));
        }).RequireAuthorization();

        bg.MapPost("/", async (BreakGlassRequestBody req, AdminGate gate, BreakGlassAdminService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.BreakGlassRequest, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var tenant = AdminContracts.ResolveTenant(p, req.Tenant);
            if (tenant is null) return ProblemResults.Invalid("no-tenant");
            if (string.IsNullOrWhiteSpace(req.Justification) || req.ScopedResourceTypes.Count == 0)
                return ProblemResults.Invalid("justification-and-scope-required");

            var g = await svc.RequestAsync(AdminContracts.Actor(p), tenant, req.ReasonCode, req.Justification,
                req.ScopedResourceTypes, req.ScopedResourceIds ?? [], req.WindowMinutes, ct);
            return Results.Created($"/api/v1/admin/break-glass/{g.GrantId}", new { g.GrantId, status = g.Status.ToString() });
        });

        bg.MapPost("/{grantId:guid}/approve", async (Guid grantId, BreakGlassRejectBody? body, AdminGate gate, BreakGlassAdminService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.BreakGlassApprove, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var tenant = AdminContracts.ResolveTenant(p, body?.Tenant);
            if (tenant is null) return ProblemResults.Invalid("no-tenant");

            var r = await svc.ApproveAsync(AdminContracts.Actor(p), tenant, grantId, ct);
            if (r.Ok) return Results.Ok(new { grantId, status = r.Grant!.Status.ToString() });
            // A self-approval attempt is a dual-control violation.
            return r.ReasonCode == "self-approval-denied"
                ? ProblemResults.Conflict(r.ReasonCode ?? "conflict")
                : ProblemResults.Invalid(r.ReasonCode ?? "error");
        });

        bg.MapPost("/{grantId:guid}/reject", async (Guid grantId, BreakGlassRejectBody req, AdminGate gate, BreakGlassAdminService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.BreakGlassApprove, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var tenant = AdminContracts.ResolveTenant(p, req.Tenant);
            if (tenant is null) return ProblemResults.Invalid("no-tenant");
            var r = await svc.RejectAsync(AdminContracts.Actor(p), tenant, grantId, req.Reason, ct);
            return r.Ok ? Results.NoContent() : ProblemResults.Invalid(r.ReasonCode ?? "error");
        });

        bg.MapPost("/{grantId:guid}/activate", async (Guid grantId, BreakGlassActivateBody req, AdminGate gate, BreakGlassAdminService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.BreakGlassRequest, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var tenant = AdminContracts.ResolveTenant(p, req.Tenant);
            if (tenant is null) return ProblemResults.Invalid("no-tenant");
            var r = await svc.ActivateAsync(AdminContracts.Actor(p), tenant, grantId, req.StepUpSatisfied, ct);
            return r.Ok
                ? Results.Ok(new { grantId, status = r.Grant!.Status.ToString(), r.Grant.ExpiresAt })
                : ProblemResults.Invalid(r.ReasonCode ?? "error");
        });

        // Record + evaluate an access under a grant. 200 within scope, 403 out of scope (no field-deny bypass).
        bg.MapPost("/{grantId:guid}/access", async (Guid grantId, BreakGlassAccessBody req, AdminGate gate, BreakGlassAdminService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.BreakGlassRequest, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var tenant = AdminContracts.ResolveTenant(p, req.Tenant);
            if (tenant is null) return ProblemResults.Invalid("no-tenant");

            var granted = await svc.RecordAccessAsync(AdminContracts.Actor(p), tenant, grantId, req.ResourceType, req.ResourceId, req.Action, ct);
            return granted
                ? Results.Ok(new { grantId, granted = true })
                : Results.Problem(statusCode: 403, title: "break-glass-out-of-scope", type: "urn:hbmp:break-glass-scope");
        });

        // -------------------------------------------------- Governance dashboards (tenant-scoped, audited view)
        var dash = app.MapGroup("/api/v1/admin/dashboards").WithTags("admin-dashboards");
        dash.MapGet("/break-glass", async (string? tenant, AdminGate gate, DashboardService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.ReadDashboard, ct);
            if (denied is not null) return denied;
            var t = AdminContracts.ResolveTenant(gate.Principal!, tenant);
            if (t is null) return ProblemResults.Invalid("no-tenant");
            return Results.Ok(await svc.BreakGlassAsync(AdminContracts.Actor(gate.Principal!), t, ct));
        });
        dash.MapGet("/access-review", async (string? tenant, AdminGate gate, DashboardService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.ReadDashboard, ct);
            if (denied is not null) return denied;
            var t = AdminContracts.ResolveTenant(gate.Principal!, tenant);
            if (t is null) return ProblemResults.Invalid("no-tenant");
            return Results.Ok(await svc.AccessReviewAsync(AdminContracts.Actor(gate.Principal!), t, ct));
        });
        dash.MapGet("/sod-violations", async (string? tenant, AdminGate gate, DashboardService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.ReadDashboard, ct);
            if (denied is not null) return denied;
            var t = AdminContracts.ResolveTenant(gate.Principal!, tenant);
            if (t is null) return ProblemResults.Invalid("no-tenant");
            return Results.Ok(await svc.SodViolationsAsync(AdminContracts.Actor(gate.Principal!), t, ct));
        });
    }
}
