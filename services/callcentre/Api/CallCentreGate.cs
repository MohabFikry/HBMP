using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.CallCentre.Api;

/// <summary>The call-centre access decision. The Call Centre is MemberScoped (design 37 §3) so there is no branch
/// or per-record ABAC here — the coarse check is role + tenant + scope, run at the POLICY layer (not just in the
/// controller) so every deny is audited by the engine. The DISTINCTIVE control of this portal — "verify before you
/// disclose" — is enforced separately by <c>VerificationService</c> on the disclose/act endpoints; this gate only
/// answers "may this role do this kind of action at all". Returns a ready 403 when denied, else null.</summary>
public sealed class CallCentreGate(IHbmpPrincipalAccessor me, IAuthorizationEngine engine)
{
    public async Task<IResult?> CheckAsync(string action, string purpose, CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null)
            return GateResults.Unauthenticated();

        var resource = new ResourceRef { Type = CallCentrePolicies.Resource, TenantId = p.TenantId };
        var decision = await engine.EvaluateAsync(new AuthzRequest(p, action, resource, purpose), ct);
        if (decision.IsAllowed) return null;

        return GateResults.Forbidden("urn:hbmp:callcentre-access-denied", detail: "You are not permitted to perform this call-centre action.", reason: decision.ReasonCode);
    }

    public HbmpPrincipal? Principal => me.Principal;
    public string? Tenant => me.Principal?.TenantId;
    public string? Subject => me.Principal?.Subject;
}
