using Microsoft.AspNetCore.Authorization;

namespace Mersal.Auth.Authorization;

/// <summary>
/// Default-deny handler for <see cref="ScopeRequirement"/>: allow only when the principal
/// holds the scope AND (if required) satisfied MFA. Emits an auth audit event on denial.
/// </summary>
public sealed class ScopeAuthorizationHandler(IAuthEventSink events)
    : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            // Unauthenticated → leave unhandled (401), no principal to build.
            return Task.CompletedTask;
        }

        var principal = HbmpPrincipal.FromClaims(context.User);

        if (requirement.RequireMfa && !principal.MfaSatisfied)
        {
            events.Record(new AuthEvent(
                AuthEventKind.MfaRequiredButMissing, principal.Subject,
                Reason: $"scope '{requirement.Scope}' requires MFA",
                SessionId: principal.SessionId, SourceIp: principal.SourceIp, Scope: requirement.Scope));
            context.Fail(new AuthorizationFailureReason(this, "mfa_required"));
            return Task.CompletedTask;
        }

        // Any-of: holding one accepted scope is enough (see ScopeRequirement).
        if (!requirement.Scopes.Any(principal.HasScope))
        {
            events.Record(new AuthEvent(
                AuthEventKind.AuthorizationDenied, principal.Subject,
                Reason: $"missing scope '{requirement.Scope}'",
                SessionId: principal.SessionId, SourceIp: principal.SourceIp, Scope: requirement.Scope));
            context.Fail(new AuthorizationFailureReason(this, "insufficient_scope"));
            return Task.CompletedTask;
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
