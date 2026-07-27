using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Provider.Api;

/// <summary>Phase 19.1b — the network-administration decision. A tier and its assignments carry no PHI, so this
/// is a straight role+scope+tenant check with no ABAC resource resolution; the separation that matters is
/// between the Network Team, who decide WHICH tier a provider sits in, and policy administration, who decide
/// what a member pays AT a tier (<see cref="ProviderPolicies.Actions.NetworkAdmin"/> documents why). Returns a
/// ready 403 when denied, else null.</summary>
public sealed class NetworkTierGate(IHbmpPrincipalAccessor me, IAuthorizationEngine engine)
{
    public async Task<IResult?> CheckAsync(CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null) return GateResults.Unauthenticated();

        var resource = new ResourceRef { Type = "network_tier", TenantId = p.TenantId };
        var decision = await engine.EvaluateAsync(
            new AuthzRequest(p, ProviderPolicies.Actions.NetworkAdmin, resource), ct);
        if (decision.IsAllowed) return null;

        return GateResults.Forbidden("urn:hbmp:network-tier-access-denied",
            detail: "Network tiers are administered by the Network Team.", reason: decision.ReasonCode);
    }

    public HbmpPrincipal? Principal => me.Principal;
    public string? Subject => me.Principal?.Subject;
    public string? TenantId => me.Principal?.TenantId;
}
