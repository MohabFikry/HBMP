using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Finance.Api;

/// <summary>The finance-access decision. Finance holds ONLY the finance actions; there is no rule granting Finance
/// any clinical action, so a diagnosis/EMR read is default-denied (finance ≠ diagnosis, 11-permission-matrix §3.2).
/// The engine audits every deny and every sensitive allow (approve, export). Returns a ready 403 when denied, else
/// null. Tenant is taken from the caller's principal.</summary>
public sealed class FinanceGate(IHbmpPrincipalAccessor me, IAuthorizationEngine engine)
{
    public async Task<IResult?> CheckAsync(string action, CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null)
            return GateResults.Unauthenticated();

        var resource = new ResourceRef { Type = FinancePolicies.Resource, TenantId = p.TenantId };
        var decision = await engine.EvaluateAsync(new AuthzRequest(p, action, resource), ct);
        if (decision.IsAllowed) return null;

        return GateResults.Forbidden("urn:hbmp:finance-access-denied", detail: "You are not permitted to perform this finance action.", reason: decision.ReasonCode);
    }

    public string? Tenant => me.Principal?.TenantId;
    public string? Subject => me.Principal?.Subject;
    public string? Roles => me.Principal is null ? null : string.Join(',', me.Principal.Roles);
}
