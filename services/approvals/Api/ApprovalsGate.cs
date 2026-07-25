using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Approvals.Api;

/// <summary>The approvals-access decision. Unlike the clinical services there is NO treating relationship — the
/// Medical Approval team / Director read the record for OVERSIGHT (11-permission-matrix §3.2), so the gate is a
/// tenant-scoped role+scope check via the shared engine. The engine audits every deny and every sensitive allow
/// (the review/decision actions are flagged Sensitive → PHI-read/decision audit). Returns a ready 403 when denied,
/// else null. A stated <paramref name="purpose"/> (PUR for the clinical review) is recorded on the audit event.</summary>
public sealed class ApprovalsGate(IHbmpPrincipalAccessor me, IAuthorizationEngine engine)
{
    public async Task<IResult?> CheckAsync(string action, string? resourceId, string purpose, CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null)
            return Results.Problem(statusCode: 401, title: "unauthenticated", type: "urn:hbmp:unauthenticated");

        var resource = new ResourceRef
        {
            Type = ApprovalsPolicies.Resource, Id = resourceId, TenantId = p.TenantId,
        };
        var decision = await engine.EvaluateAsync(new AuthzRequest(p, action, resource, purpose), ct);
        if (decision.IsAllowed) return null;

        return Results.Problem(
            statusCode: 403, title: "access-denied", type: "urn:hbmp:approvals-access-denied",
            detail: "You are not permitted to perform this approvals action.",
            extensions: new Dictionary<string, object?> { ["reason"] = decision.ReasonCode });
    }
}
