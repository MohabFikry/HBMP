using Mersal.Auth;
using Mersal.Authz;
using Mersal.Pharmacy.Infrastructure;

namespace Mersal.Pharmacy.Api;

/// <summary>The prescribe/refer access decision (US-033/US-034): the prescriber must have a treating relationship
/// with the beneficiary. Row-level truth from emr-service (<see cref="ITreatingRelationshipClient"/>), enforced by
/// the shared authorization engine's treating-relationship ABAC condition. Returns a ready 403 when denied.</summary>
public sealed class PharmacyGate(IHbmpPrincipalAccessor me, IAuthorizationEngine engine, ITreatingRelationshipClient treating)
{
    public async Task<IResult?> CheckAsync(string action, string resourceType, string? resourceId, Guid beneficiaryId, string? bearerToken, CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null)
            return GateResults.Unauthenticated();

        var treats = await treating.TreatsAsync(beneficiaryId, bearerToken, ct);
        var treatingSet = new HashSet<string>(StringComparer.Ordinal);
        if (treats) treatingSet.Add(beneficiaryId.ToString());

        var resource = new ResourceRef
        {
            Type = resourceType, Id = resourceId, TenantId = p.TenantId,
            BeneficiaryId = beneficiaryId.ToString(), TreatingBeneficiaryIds = treatingSet,
        };
        var decision = await engine.EvaluateAsync(new AuthzRequest(p, action, resource, "prescribing"), ct);
        if (decision.IsAllowed) return null;

        return GateResults.Forbidden("urn:hbmp:pharmacy-access-denied", detail: "You do not have a treating relationship with this patient.", reason: decision.ReasonCode);
    }
}
