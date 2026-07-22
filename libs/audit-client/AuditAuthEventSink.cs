using Mersal.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Audit.Client;

/// <summary>
/// Bridges <c>libs/auth</c> auth events into the durable audit trail — closing the 0.2 stub where
/// <see cref="IAuthEventSink"/> was a no-op. Login/failure/MFA-missing/deny/logout events become
/// hash-chained audit records like every other mutation (19-audit-strategy.md §11).
/// Registered as a singleton; resolves the (scoped) <see cref="IAuditClient"/> per call.
/// </summary>
public sealed class AuditAuthEventSink(IServiceScopeFactory scopeFactory) : IAuthEventSink
{
    public void Record(AuthEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        using var scope = scopeFactory.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditClient>();

        var (action, severity) = evt.Kind switch
        {
            AuthEventKind.LoginSuccess => (AuditAction.Login, AuditSeverity.Info),
            AuthEventKind.Logout => (AuditAction.Login, AuditSeverity.Info),
            AuthEventKind.LoginFailure => (AuditAction.Login, AuditSeverity.Warning),
            AuthEventKind.TokenRejected => (AuditAction.Login, AuditSeverity.Warning),
            AuthEventKind.MfaRequiredButMissing => (AuditAction.Login, AuditSeverity.Warning),
            AuthEventKind.AuthorizationDenied => (AuditAction.Grant, AuditSeverity.Warning),
            _ => (AuditAction.Login, AuditSeverity.Info),
        };

        // Enqueue (cheap: an outbox insert) and complete before the scope is disposed. Auth events carry no PHI.
        audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "identity",
            EntityId = evt.Subject ?? "(anonymous)",
            Action = action,
            ActorUserId = evt.Subject,
            SessionId = evt.SessionId,
            DecisionOutcome = evt.Kind.ToString(),
            DecisionReasonCode = evt.Reason,
            AfterState = evt.Scope is null ? null : $"{{\"scope\":\"{evt.Scope}\"}}",
            Severity = severity,
        }).AsTask().GetAwaiter().GetResult();
    }
}
