using Microsoft.AspNetCore.Authorization;

namespace Mersal.Auth.Authorization;

/// <summary>Requires an MFA-backed token, independent of any scope.</summary>
public sealed class MfaRequirement : IAuthorizationRequirement;

/// <summary>Default-deny handler for <see cref="MfaRequirement"/>; audits missing MFA.</summary>
public sealed class MfaAuthorizationHandler(IAuthEventSink events)
    : AuthorizationHandler<MfaRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, MfaRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return Task.CompletedTask;

        var principal = HbmpPrincipal.FromClaims(context.User);
        if (principal.MfaSatisfied)
        {
            context.Succeed(requirement);
        }
        else
        {
            events.Record(new AuthEvent(
                AuthEventKind.MfaRequiredButMissing, principal.Subject,
                Reason: "endpoint requires MFA",
                SessionId: principal.SessionId, SourceIp: principal.SourceIp));
            context.Fail(new AuthorizationFailureReason(this, "mfa_required"));
        }
        return Task.CompletedTask;
    }
}
