using Mersal.Auth;
using Mersal.Authz;
using Mersal.Orders.Infrastructure;

namespace Mersal.Orders.Api;

/// <summary>The order-access decision (US-032): the ordering doctor must have a treating relationship with the
/// beneficiary. The row-level truth comes from emr-service (<see cref="ITreatingRelationshipClient"/>); it is
/// handed to the shared authorization engine's treating-relationship ABAC condition. Returns a ready 403 when
/// denied (engine audits it), else null.</summary>
public sealed class OrdersGate(IHbmpPrincipalAccessor me, IAuthorizationEngine engine, ITreatingRelationshipClient treating)
{
    public async Task<IResult?> CheckAsync(string action, string? resourceId, Guid beneficiaryId, string? bearerToken, CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null)
            return Results.Problem(statusCode: 401, title: "unauthenticated", type: "urn:hbmp:unauthenticated");

        var treats = await treating.TreatsAsync(beneficiaryId, bearerToken, ct);
        var treatingSet = new HashSet<string>(StringComparer.Ordinal);
        if (treats) treatingSet.Add(beneficiaryId.ToString());

        var resource = new ResourceRef
        {
            Type = "investigation_order", Id = resourceId, TenantId = p.TenantId,
            BeneficiaryId = beneficiaryId.ToString(), TreatingBeneficiaryIds = treatingSet,
        };
        var decision = await engine.EvaluateAsync(new AuthzRequest(p, action, resource, "ordering"), ct);
        if (decision.IsAllowed) return null;

        return Results.Problem(
            statusCode: 403, title: "access-denied", type: "urn:hbmp:orders-access-denied",
            detail: "You do not have a treating relationship with this patient.",
            extensions: new Dictionary<string, object?> { ["reason"] = decision.ReasonCode });
    }
}
