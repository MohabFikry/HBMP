using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Admin.Api;

/// <summary>Phase 14.2 — staff↔branch assignment admin + the caller's active-branch context. Assignment
/// writes/reads are gated by <see cref="AdminGate"/> (Org Admin / Network Team, audited); the self-service
/// <c>/me</c> endpoints resolve the caller's own permitted set + validate an active-branch switch. THE
/// INVARIANT: X-Active-Branch is never trusted — a branch outside the permitted set is 403 + audited.</summary>
public static class BranchAssignmentEndpoints
{
    public static void MapBranchAssignments(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/admin").WithTags("admin-branches");

        // Assign a branch (Home or Additional). A second active Home → 409 home-exists.
        admin.MapPost("/users/{subject}/branches", async (string subject, AssignBranchRequest req, AdminGate gate, BranchAssignmentService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.GrantRole, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var tenant = AdminContracts.ResolveTenant(p, req.Tenant);
            if (tenant is null) return Results.BadRequest(new { error = "no-tenant" });
            if (!Enum.TryParse<BranchAssignmentType>(req.AssignmentType, out var type))
                return Results.BadRequest(new { error = "unknown-assignment-type" });

            var r = await svc.AssignAsync(AdminContracts.Actor(p), tenant, subject, req.BranchId, type, req.ValidFrom, req.ValidTo, ct);
            if (r.Ok)
                return Results.Created($"/api/v1/admin/users/{subject}/branches/{r.Assignment!.AssignmentId}", BranchAssignmentView.Of(r.Assignment));
            return Results.Conflict(new { error = r.ReasonCode, detail = "the user already has an active home branch" });
        });

        // Revoke a branch assignment (soft — effective on the user's next request).
        admin.MapPost("/users/{subject}/branches/revoke", async (string subject, RevokeBranchRequest req, AdminGate gate, BranchAssignmentService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.RevokeRole, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var tenant = AdminContracts.ResolveTenant(p, req.Tenant);
            if (tenant is null) return Results.BadRequest(new { error = "no-tenant" });

            var ok = await svc.RevokeAsync(AdminContracts.Actor(p), tenant, req.AssignmentId, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        // List a user's branch assignments (audited admin read).
        admin.MapGet("/users/{subject}/branches", async (string subject, string? tenant, AdminGate gate, BranchAssignmentService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.ReadAccess, ct);
            if (denied is not null) return denied;
            var t = AdminContracts.ResolveTenant(gate.Principal!, tenant);
            if (t is null) return Results.BadRequest(new { error = "no-tenant" });
            var rows = await svc.ListAsync(t, subject, ct);
            return Results.Ok(rows.Select(BranchAssignmentView.Of));
        });

        // --- self-service context (any authenticated user) --------------------------------------
        var me = app.MapGroup("/api/v1/me").RequireAuthorization().WithTags("me-branches");

        // The caller's home + permitted branches (drives the branch switcher).
        me.MapGet("/branches", async (IHbmpPrincipalAccessor accessor, BranchAssignmentService svc, CancellationToken ct) =>
        {
            var p = accessor.Principal;
            if (p?.TenantId is null) return Results.Problem(statusCode: 403, title: "no tenant scope on principal");
            var res = await svc.ResolveAsync(p.TenantId, p.Subject, requested: null, ct);
            return Results.Ok(new
            {
                homeBranch = res.Outcome == BranchAssignmentRules.ResolveOutcome.ResolvedHome ? res.BranchId : null,
                permittedBranches = res.Permitted,
            });
        });

        // Switch the active branch (validated against the permitted set → ActiveBranchSwitched or 403).
        me.MapPost("/active-branch", async (SwitchBranchRequest req, IHbmpPrincipalAccessor accessor, BranchAssignmentService svc, CancellationToken ct) =>
        {
            var p = accessor.Principal;
            if (p?.TenantId is null) return Results.Problem(statusCode: 403, title: "no tenant scope on principal");
            var res = await svc.SwitchAsync(AdminContracts.Actor(p), p.TenantId, p.Subject, req.BranchId, ct);
            if (!res.Allowed)
                return Results.Problem(statusCode: 403, title: "branch-not-permitted", type: "urn:hbmp:branch-scope-denied",
                    detail: "the requested branch is not in your permitted set");
            return Results.Ok(new { activeBranch = res.BranchId, permittedBranches = res.Permitted });
        });
    }
}
