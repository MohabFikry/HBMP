using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;

namespace Mersal.Admin.Api;

/// <summary>
/// Phase 19.5 — user↔payer restriction admin + the caller's own payer scope (design 38 §6).
///
/// <para><c>GET /api/v1/me/payers</c> is the endpoint policy-service reads through <c>IPayerDirectory</c>. It
/// returns <c>unrestricted</c> as an explicit boolean rather than leaving the caller to infer it from an empty
/// array: "no restrictions" and "restricted to nothing" are opposite answers with identical JSON if the flag is
/// missing, and inferring the wrong one during an outage would widen access.</para>
/// </summary>
public static class PayerAssignmentEndpoints
{
    public static void MapPayerAssignments(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/admin").WithTags("admin-payers")
            .RequireAuthorization(HbmpPolicies.Scope("admin:read"));
        var adminWrite = admin.MapGroup("").RequireAuthorization(HbmpPolicies.Scope("admin:write"));

        // Restrict a user to a payer. A duplicate live restriction → 409.
        adminWrite.MapPost("/users/{subject}/payers", async (string subject, AssignPayerRequest req,
            AdminGate gate, PayerAssignmentService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.GrantRole, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var scope = gate.BindTenant(req.Tenant);
            if (!scope.IsAllowed) return scope.ToProblem();

            var r = await svc.AssignAsync(AdminContracts.Actor(p), scope.Tenant!, subject, req.PayerId,
                req.ValidFrom, req.ValidTo, ct);
            return r.Ok
                ? Results.Created($"/api/v1/admin/users/{subject}/payers/{r.Assignment!.AssignmentId}",
                    PayerAssignmentView.Of(r.Assignment))
                : ProblemResults.Conflict(r.ReasonCode ?? "conflict", "the user is already restricted to this payer");
        });

        // Revoke a restriction (soft — effective on the user's next request, within the directory TTL).
        adminWrite.MapPost("/users/{subject}/payers/revoke", async (string subject, RevokePayerRequest req,
            AdminGate gate, PayerAssignmentService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.RevokeRole, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var scope = gate.BindTenant(req.Tenant);
            if (!scope.IsAllowed) return scope.ToProblem();

            var ok = await svc.RevokeAsync(AdminContracts.Actor(p), scope.Tenant!, req.AssignmentId, ct);
            return ok ? Results.NoContent()
                      : Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
        });

        // List a user's payer restrictions (audited admin read).
        admin.MapGet("/users/{subject}/payers", async (string subject, string? tenant,
            AdminGate gate, PayerAssignmentService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.ReadAccess, ct);
            if (denied is not null) return denied;
            var scope = gate.BindTenant(tenant);
            if (!scope.IsAllowed) return scope.ToProblem();
            var rows = await svc.ListAsync(scope.Tenant!, subject, ct);
            return Results.Ok(rows.Select(PayerAssignmentView.Of));
        })
        .Produces<IEnumerable<PayerAssignmentView>>();

        // --- self-service scope (any authenticated user; read by IPayerDirectory) -----------------------
        var me = app.MapGroup("/api/v1/me").RequireAuthorization().WithTags("me-payers");

        me.MapGet("/payers", async (IHbmpPrincipalAccessor accessor, PayerAssignmentService svc, CancellationToken ct) =>
        {
            var p = accessor.Principal;
            if (p?.TenantId is null) return Results.Problem(statusCode: 403, title: "no tenant scope on principal");
            var ids = await svc.EffectivePayerIdsAsync(p.TenantId, p.Subject, ct);
            return Results.Ok(new PayerScopeView(ids.Count == 0, [.. ids]));
        })
        .Produces<PayerScopeView>();
    }
}

public sealed record AssignPayerRequest(Guid PayerId, DateOnly ValidFrom, DateOnly? ValidTo, string? Tenant = null);

public sealed record RevokePayerRequest(Guid AssignmentId, string? Tenant = null);

public sealed record PayerAssignmentView(
    Guid AssignmentId, Guid PayerId, DateOnly ValidFrom, DateOnly? ValidTo, string Status)
{
    public static PayerAssignmentView Of(Mersal.Admin.Domain.UserPayerAssignment a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new(a.AssignmentId, a.PayerId, a.ValidFrom, a.ValidTo, a.Status.ToString());
    }
}
