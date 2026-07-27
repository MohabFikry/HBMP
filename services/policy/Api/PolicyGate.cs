using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Policy.Api;

/// <summary>The policy-administration decision (phase 19). Benefit configuration carries no PHI, so the gate is
/// a straight role+scope+tenant check with no ABAC resource resolution — the interesting separation is between
/// authoring a product (<c>policy:admin</c>) and administering a member against it (<c>policy:write</c>), which
/// <see cref="PolicyPolicies"/> documents. Returns a ready 403 when denied, else null.</summary>
public sealed class PolicyGate(IHbmpPrincipalAccessor me, IAuthorizationEngine engine)
{
    public async Task<IResult?> CheckAsync(string action, CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null)
            return GateResults.Unauthenticated();

        var resource = new ResourceRef { Type = PolicyPolicies.Resource, TenantId = p.TenantId };
        var decision = await engine.EvaluateAsync(new AuthzRequest(p, action, resource), ct);
        if (decision.IsAllowed) return null;

        return GateResults.Forbidden("urn:hbmp:policy-access-denied",
            detail: "You are not permitted to perform this policy-administration action.", reason: decision.ReasonCode);
    }

    public HbmpPrincipal? Principal => me.Principal;
    public string? Subject => me.Principal?.Subject;
    /// <summary>The caller's subject as a Guid when it is one — the signature columns are uuid.</summary>
    public Guid? SubjectId => Guid.TryParse(me.Principal?.Subject, out var id) ? id : null;
}
