using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Reporting.Api;

/// <summary>The reporting-access decision. Access is split by data zone (operational / clinical-coded / financial)
/// so the permission matrix is enforced in AUTHZ: the finance role holds only the financial action, so a
/// diagnosis-bearing report is default-denied to it. The engine audits every deny and every sensitive allow
/// (exports). Returns a ready 403 when denied, else null. Tenant is taken from the caller's principal.</summary>
public sealed class ReportingGate(IHbmpPrincipalAccessor me, IAuthorizationEngine engine)
{
    public async Task<IResult?> CheckAsync(string action, CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null)
            return GateResults.Unauthenticated();

        var resource = new ResourceRef { Type = ReportingPolicies.Resource, TenantId = p.TenantId };
        var decision = await engine.EvaluateAsync(new AuthzRequest(p, action, resource), ct);
        if (decision.IsAllowed) return null;

        return GateResults.Forbidden("urn:hbmp:reporting-access-denied", detail: "You are not permitted to read this report zone.", reason: decision.ReasonCode);
    }

    public string? Tenant => me.Principal?.TenantId;
}
