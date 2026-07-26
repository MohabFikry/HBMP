using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.CallCentre.Infrastructure;

namespace Mersal.CallCentre.Api;

/// <summary>Phase 15.6 — the PHI-free call-centre KPI endpoint (supervisor/manager scope). Aggregate-only: calls
/// handled, average handle time, first-contact resolution, verification-failure + abandoned rates, appointment
/// actions, and the reason mix — no member identity, no clinical field. Suitable for the dashboard contracts.</summary>
public static class Kpis
{
    public static void MapKpis(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/call-centre/kpis", async (
            DateTimeOffset? from, DateTimeOffset? to, CallDeps deps, KpiService kpis, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.ReadTeam, "call-kpis", ct);
            if (denied is not null) return denied;

            var toT = to ?? deps.Clock.GetUtcNow();
            var fromT = from ?? toT.AddDays(-30);
            var result = await kpis.ComputeAsync(deps.Tenant ?? "unknown", fromT, toT, ct);
            await deps.AuditAsync("call_centre_kpis", $"{fromT:o}/{toT:o}", AuditAction.Read, "ReadKpis", null);
            return Results.Ok(result);
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:read"));
    }
}
