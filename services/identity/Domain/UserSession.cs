using System.Net;

namespace Mersal.Identity.Domain;

/// <summary>
/// 21.5 — a live sign-in (design 40 §6). Table: <c>identity.user_session</c>.
///
/// The device metadata exists so a person can RECOGNISE their own sessions and spot the one that is not
/// theirs; it authenticates nothing. Revocation is soft and attributed, so "who ended this and when" stays
/// answerable after the fact.
/// </summary>
public sealed class UserSession
{
    public Guid SessionId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>The membership this session acts under (21.1c). Null for a legacy membership-less session.</summary>
    public Guid? MembershipId { get; set; }

    public string? UserAgent { get; set; }
    public IPAddress? IpAddress { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public string? RevokeReason { get; set; }

    /// <summary>Whether this session may still act.</summary>
    public bool IsLive => RevokedAt is null;
}

/// <summary>Why a login attempt failed, at the coarseness the store keeps.</summary>
public static class LoginFailureReasons
{
    /// <summary>
    /// The single reason used for BOTH "no such user" and "wrong password".
    ///
    /// Keeping them apart in the store would be harmless until someone surfaced the distinction in a
    /// support screen or an error message — at which point it becomes a user-enumeration oracle. It is
    /// stored coarse so it cannot leak by being displayed.
    /// </summary>
    public const string BadCredentials = "bad-credentials";

    public const string LockedOut = "locked-out";
    public const string Inactive = "inactive";
    public const string TwoFactorFailed = "two-factor-failed";
}

/// <summary>21.5 — one sign-in attempt. Never carries password material of any kind.
/// Table: <c>identity.login_attempt</c>.</summary>
public sealed class LoginAttempt
{
    public long AttemptId { get; set; }

    /// <summary>Null when the username did not resolve — the attempt still matters (that is what credential
    /// stuffing looks like), but there is no identity to attach it to.</summary>
    public Guid? UserId { get; set; }

    /// <summary>What was typed in the username box. Never what was typed in the password box.</summary>
    public string UsernameTried { get; set; } = string.Empty;

    public bool Succeeded { get; set; }
    public string? FailureReason { get; set; }
    public IPAddress? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTimeOffset AttemptedAt { get; set; }
}
