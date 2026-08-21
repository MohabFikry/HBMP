using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Mersal.Time;

namespace Mersal.Provider.Api;

/// <summary>How many of a provider's credentials are currently good for, and how many are about to stop being.</summary>
public sealed record CredentialCountsView(int Valid, int ExpiringSoon, int Expired);

/// <summary>One provider's performance counters (2b.3), as the Network Team and the provider itself read them.</summary>
public sealed record ProviderMetricsView(
    Guid ProviderId, string Status, int ActiveContracts, int ServicesOffered,
    CredentialCountsView Credentials, int OrdersFulfilled, double? AvgTurnaroundHours);

/// <summary>
/// The network roll-up — how many providers the tenant has, and in what standing.
/// </summary>
/// <remarks>
/// <para>33.7 — this endpoint has existed since phase 2b and had no Kong route, so nothing could reach it.
/// The SPA's Performance screen produced the identical four numbers by fetching the provider directory and
/// counting rows whose <c>status.label.en</c> equalled "Active" — a tally of a DISPLAY STRING, over whatever
/// the directory projection happened to return, computed past the 403 this endpoint gives a provider-scoped
/// caller. The authorization below is not decoration: a provider user must not learn the shape of the
/// network they compete in, and a count assembled in the browser enforces nothing.</para>
/// </remarks>
public sealed record NetworkMetricsView(int Total, int Active, int Suspended, int Terminated);

/// <summary>Provider performance metrics (2b.3). Per-provider counters are provider-scoped — a provider
/// user sees ONLY their own numbers (ABAC PO + RLS); the network-wide roll-up is Network-Team only. Order
/// throughput / turnaround are populated by fulfillment events in phases 5/6; the fields exist now so the
/// reporting-service (phase 8) contract is stable.</summary>
public static class MetricsEndpoints
{
    public static void MapMetrics(this WebApplication app)
    {
        var read = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("provider:read"));

        // Per-provider, provider-scoped.
        read.MapGet("/providers/{id:guid}/metrics", async (Guid id, ProviderDbContext db, ProviderAccessGuard guard, IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var p = await db.Providers.AsNoTracking()
                .Include(x => x.Contracts).ThenInclude(c => c.ServiceLines)
                .Include(x => x.Credentials)
                .FirstOrDefaultAsync(x => x.ProviderId == id && x.TenantId == tenant && !x.IsDeleted, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            var decision = await guard.AuthorizeAsync(me.Require(), p.TenantId, p.ProviderId.ToString(), ct);
            if (!decision.IsAllowed) return Results.Problem(statusCode: 403, title: "metrics access denied", detail: decision.ReasonCode);

            var today = calendar.Today();   // 18.A3
            var inEffect = p.Contracts.Where(c => ContractRules.InEffect(c, today)).ToList();
            return Results.Ok(new ProviderMetricsView(
                p.ProviderId,
                p.Status.ToString(),
                inEffect.Count,
                inEffect.SelectMany(c => c.ServiceLines).Select(l => l.Code).Distinct().Count(),
                new CredentialCountsView(
                    p.Credentials.Count(c => !c.IsDeleted && CredentialRules.IsValidOn(c, today)),
                    p.Credentials.Count(c => !c.IsDeleted && CredentialRules.ExpiryReminderDue(c, today)),
                    p.Credentials.Count(c => !c.IsDeleted && c.ValidTo is { } to && to < today)),
                // Populated by phases 5/6 fulfillment events; surfaced now for a stable reporting contract.
                OrdersFulfilled: 0,
                AvgTurnaroundHours: null));
        })
        .Produces<ProviderMetricsView>();

        // Network-wide roll-up — Network Team only (provider-scoped users are blocked at the token layer).
        read.MapGet("/metrics", async (ProviderDbContext db, IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var principal = me.Require();
            if (ProviderAccessGuard.IsProviderScoped(principal))
                return Results.Problem(statusCode: 403, title: "network roll-up is not visible to provider users");

            var tenant = principal.TenantId;
            var providers = await db.Providers.AsNoTracking().Where(p => p.TenantId == tenant && !p.IsDeleted).ToListAsync(ct);
            return Results.Ok(new NetworkMetricsView(
                providers.Count,
                providers.Count(p => p.Status == ProviderStatus.Active),
                providers.Count(p => p.Status == ProviderStatus.Suspended),
                providers.Count(p => p.Status == ProviderStatus.Terminated)));
        })
        .Produces<NetworkMetricsView>();
    }
}
