using Mersal.Auth;
using Mersal.Authz;
using Mersal.Orders.Domain;

namespace Mersal.Orders.Api;

/// <summary>Fulfillment-side authorization (phase 5). A lab/imaging provider may read their OWN provider's work
/// queue and consume lines for their OWN provider — enforced by the shared engine's provider-ownership ABAC rule
/// (<see cref="ProviderPolicies"/>/<see cref="OrdersPolicies"/>), which audits every allow/deny. The Lab-vs-Imaging
/// capability match is a separate domain check (<see cref="ProviderCapability"/>) so a lab tech can never fulfil an
/// imaging order and vice-versa. Returns a ready problem result when denied, else null.</summary>
public sealed class FulfillmentGate(IHbmpPrincipalAccessor me, IAuthorizationEngine engine)
{
    /// <summary>May the caller read a fulfilling provider's queue at all? (role + scope + owns a provider identity.)</summary>
    public async Task<IResult?> AuthorizeQueueAsync(CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null) return GateResults.Unauthenticated();
        if (string.IsNullOrWhiteSpace(p.ProviderId))
            return Deny("You are not associated with a fulfilling provider.");

        var resource = new ResourceRef { Type = "provider_queue", TenantId = p.TenantId, ProviderId = p.ProviderId };
        var decision = await engine.EvaluateAsync(new AuthzRequest(p, ProviderPolicies.Actions.QueueRead, resource, "fulfillment"), ct);
        return decision.IsAllowed ? null : Deny(decision.ReasonCode);
    }

    /// <summary>May the caller consume this order's lines? Provider-ownership (own provider) AND capability
    /// (their role covers the order's type). Anything else → audited 403.</summary>
    public async Task<IResult?> AuthorizeConsumeAsync(OrderType orderType, CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null) return GateResults.Unauthenticated();
        if (string.IsNullOrWhiteSpace(p.ProviderId))
            return Deny("You are not associated with a fulfilling provider.");

        var resource = new ResourceRef { Type = "order_line", TenantId = p.TenantId, ProviderId = p.ProviderId };
        var decision = await engine.EvaluateAsync(new AuthzRequest(p, OrdersPolicies.Consume, resource, "fulfillment"), ct);
        if (!decision.IsAllowed) return Deny(decision.ReasonCode);

        if (!ProviderCapability.CanFulfil(p.Roles, orderType))
            return Deny($"A {string.Join("/", p.Roles)} may not fulfil {orderType} orders.");
        return null;
    }

    private static IResult Deny(string reason) => GateResults.Forbidden("urn:hbmp:orders-access-denied", detail: "You are not authorized to fulfil this order.", reason: reason);
}
