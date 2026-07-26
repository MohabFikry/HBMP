using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Claims.Api;

/// <summary>The claims-access decision. The claims roles hold ONLY the claims actions; there is no rule granting any
/// clinical action, so a diagnosis/EMR read is default-denied (claims ≠ diagnosis, 11-permission-matrix §3.2). The
/// engine audits every deny and every sensitive allow (review, decide, adjust, export, settle). Returns a ready 403
/// when denied, else null. Tenant + provider come from the caller's principal, so provider users are isolated to
/// their own claims (ABAC provider-ownership + RLS).</summary>
public sealed class ClaimsGate(IHbmpPrincipalAccessor me, IAuthorizationEngine engine)
{
    public async Task<IResult?> CheckAsync(string action, CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null)
            return GateResults.Unauthenticated();

        var resource = new ResourceRef { Type = ClaimsPolicies.Resource, TenantId = p.TenantId, ProviderId = p.ProviderId };
        var decision = await engine.EvaluateAsync(new AuthzRequest(p, action, resource), ct);
        if (decision.IsAllowed) return null;

        return GateResults.Forbidden("urn:hbmp:claims-access-denied", detail: "You are not permitted to perform this claims action.", reason: decision.ReasonCode);
    }

    public string? Tenant => me.Principal?.TenantId;
    public string? Subject => me.Principal?.Subject;
    public string? ProviderId => me.Principal?.ProviderId;
    public string? Roles => me.Principal is null ? null : string.Join(',', me.Principal.Roles);
}
