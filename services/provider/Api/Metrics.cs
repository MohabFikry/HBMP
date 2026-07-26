using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Provider.Api;

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
        read.MapGet("/providers/{id:guid}/metrics", async (Guid id, ProviderDbContext db, ProviderAccessGuard guard, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var p = await db.Providers.AsNoTracking()
                .Include(x => x.Contracts).ThenInclude(c => c.ServiceLines)
                .Include(x => x.Credentials)
                .FirstOrDefaultAsync(x => x.ProviderId == id && x.TenantId == tenant && !x.IsDeleted, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            var decision = await guard.AuthorizeAsync(me.Require(), p.TenantId, p.ProviderId.ToString(), ct);
            if (!decision.IsAllowed) return Results.Problem(statusCode: 403, title: "metrics access denied", detail: decision.ReasonCode);

            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var inEffect = p.Contracts.Where(c => ContractRules.InEffect(c, today)).ToList();
            return Results.Ok(new
            {
                providerId = p.ProviderId,
                status = p.Status.ToString(),
                activeContracts = inEffect.Count,
                servicesOffered = inEffect.SelectMany(c => c.ServiceLines).Select(l => l.Code).Distinct().Count(),
                credentials = new
                {
                    valid = p.Credentials.Count(c => !c.IsDeleted && CredentialRules.IsValidOn(c, today)),
                    expiringSoon = p.Credentials.Count(c => !c.IsDeleted && CredentialRules.ExpiryReminderDue(c, today)),
                    expired = p.Credentials.Count(c => !c.IsDeleted && c.ValidTo is { } to && to < today),
                },
                // Populated by phases 5/6 fulfillment events; surfaced now for a stable reporting contract.
                ordersFulfilled = 0,
                avgTurnaroundHours = (double?)null,
            });
        });

        // Network-wide roll-up — Network Team only (provider-scoped users are blocked at the token layer).
        read.MapGet("/metrics", async (ProviderDbContext db, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var principal = me.Require();
            if (ProviderAccessGuard.IsProviderScoped(principal))
                return Results.Problem(statusCode: 403, title: "network roll-up is not visible to provider users");

            var tenant = principal.TenantId;
            var providers = await db.Providers.AsNoTracking().Where(p => p.TenantId == tenant && !p.IsDeleted).ToListAsync(ct);
            return Results.Ok(new
            {
                total = providers.Count,
                active = providers.Count(p => p.Status == ProviderStatus.Active),
                suspended = providers.Count(p => p.Status == ProviderStatus.Suspended),
                terminated = providers.Count(p => p.Status == ProviderStatus.Terminated),
            });
        });
    }
}
