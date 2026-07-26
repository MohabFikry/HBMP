using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Notification.Api;

/// <summary>The notification-access decision. Inbox / delivery / mark-read are self-service (any authenticated role,
/// tenant-scoped); the handler additionally row-filters by recipient == caller, so a user only ever touches their
/// own notifications. The ingest seam requires the system <c>notification:ingest</c> scope. Returns a ready 403 when
/// denied, else null.</summary>
public sealed class NotificationGate(IHbmpPrincipalAccessor me, IAuthorizationEngine engine)
{
    public async Task<IResult?> CheckAsync(string action, CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null)
            return GateResults.Unauthenticated();

        var resource = new ResourceRef { Type = NotificationPolicies.Resource, TenantId = p.TenantId };
        var decision = await engine.EvaluateAsync(new AuthzRequest(p, action, resource), ct);
        if (decision.IsAllowed) return null;

        return GateResults.Forbidden("urn:hbmp:notification-access-denied", detail: "You are not permitted to perform this notification action.", reason: decision.ReasonCode);
    }
}
