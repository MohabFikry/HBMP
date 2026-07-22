namespace Mersal.Auth;

/// <summary>
/// Auth-related audit events (login success/failure, MFA challenge/result, token issue/refresh/revoke,
/// logout, lockout, authz deny). In Phase 0.2 this is a stub behind an interface; Phase 0.3's
/// <c>libs/audit-client</c> provides the durable, hash-chained implementation
/// (phase-0-foundations.md 0.2 → "stub the call behind an interface until then").
/// </summary>
public interface IAuthEventSink
{
    void Record(AuthEvent evt);
}

/// <summary>An auth audit event. Kept minimal + PHI-free.</summary>
public sealed record AuthEvent(
    AuthEventKind Kind,
    string? Subject,
    string? Reason = null,
    string? SessionId = null,
    string? SourceIp = null,
    string? Scope = null);

public enum AuthEventKind
{
    LoginSuccess,
    LoginFailure,
    MfaRequiredButMissing,
    TokenRejected,
    AuthorizationDenied,
    Logout,
}

/// <summary>No-op sink used until <c>libs/audit-client</c> is wired (Phase 0.3).</summary>
public sealed class NullAuthEventSink : IAuthEventSink
{
    public static readonly NullAuthEventSink Instance = new();
    public void Record(AuthEvent evt) { /* replaced by the durable audit client in 0.3 */ }
}
