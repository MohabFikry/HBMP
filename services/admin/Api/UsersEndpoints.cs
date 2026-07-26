using Mersal.Authz;
using Microsoft.EntityFrameworkCore;
using Mersal.Admin.Infrastructure;
using Mersal.Admin.Domain;

namespace Mersal.Admin.Api;

/// <summary>User & role administration endpoints (phase 8b.1). Grant / revoke / de-provision role bindings and read
/// the access matrix — every action gated by <see cref="AdminGate"/> (Sensitive → audited allow) and SoD-checked at
/// grant time.</summary>
public static class UsersEndpoints
{
    public static void MapUsers(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/admin").WithTags("admin-users");

        // Assign a role — rejected (409) with the SoD reason if the grant breaches Segregation of Duties.
        g.MapPost("/role-bindings", async (GrantRoleRequest req, AdminGate gate, RoleAdminService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.GrantRole, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var tenant = AdminContracts.ResolveTenant(p, req.Tenant);
            if (tenant is null) return Results.BadRequest(new { error = "no-tenant" });
            if (string.IsNullOrWhiteSpace(req.Justification))
                return Results.BadRequest(new { error = "justification-required" });

            var result = await svc.GrantAsync(AdminContracts.Actor(p), tenant, req.SubjectUserId, req.Role,
                req.ScopeType, req.ProviderId, req.Justification, ct);
            if (result.Ok)
                return Results.Created($"/api/v1/admin/role-bindings/{result.Binding!.BindingId}", BindingView.Of(result.Binding));

            var conflicts = result.Violations
                .Select(v => new SodViolationView(v.HeldToken, v.ConflictingToken, v.Reason)).ToList();
            return Results.Conflict(new GrantDeniedView(result.ReasonCode ?? "denied", conflicts));
        });

        // Revoke a single binding.
        g.MapPost("/role-bindings/revoke", async (RevokeRoleRequest req, AdminGate gate, RoleAdminService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.RevokeRole, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var tenant = AdminContracts.ResolveTenant(p, req.Tenant);
            if (tenant is null) return Results.BadRequest(new { error = "no-tenant" });

            var ok = await svc.RevokeAsync(AdminContracts.Actor(p), tenant, req.BindingId, req.Reason, ct);
            return ok ? Results.NoContent() : Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
        });

        // De-provision a user everywhere (FR-IAM-010).
        g.MapPost("/users/deprovision", async (DeprovisionRequest req, AdminGate gate, RoleAdminService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.RevokeRole, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var tenant = AdminContracts.ResolveTenant(p, req.Tenant);
            if (tenant is null) return Results.BadRequest(new { error = "no-tenant" });

            await svc.DeprovisionAsync(AdminContracts.Actor(p), tenant, req.SubjectUserId, req.Reason, ct);
            return Results.NoContent();
        });

        // The access matrix — an audited admin read.
        g.MapGet("/access-matrix", async (string? tenant, AdminGate gate, RoleAdminService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.ReadAccess, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var t = AdminContracts.ResolveTenant(p, tenant);
            if (t is null) return Results.BadRequest(new { error = "no-tenant" });

            var rows = await svc.ReadAccessMatrixAsync(AdminContracts.Actor(p), t, ct);
            return Results.Ok(rows.Select(BindingView.Of));
        });

        // The effective roles a subject currently holds (empty ⇒ de-provisioned / no active grant). This is the
        // seam the auth layer / other portals consult so a de-provision denies access everywhere immediately.
        g.MapGet("/users/{subject}/effective-roles", async (string subject, string? tenant, AdminGate gate, RoleAdminService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.ReadAccess, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var t = AdminContracts.ResolveTenant(p, tenant);
            if (t is null) return Results.BadRequest(new { error = "no-tenant" });

            var roles = await svc.EffectiveRolesAsync(t, subject, ct);
            return Results.Ok(new { subject, tenant = t, roles });
        });

        // The full expanded SoD conflict matrix (10-role-matrix §7) for the admin UI — a static reference read.
        g.MapGet("/sod-matrix", async (AdminGate gate, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.ReadAccess, ct);
            if (denied is not null) return denied;
            return Results.Ok(SegregationOfDuties.ConflictRules
                .Select(r => new { r.TokenA, r.TokenB, r.Reason }));
        });
    }
}
