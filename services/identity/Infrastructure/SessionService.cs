using System.Net;
using Mersal.Authz;
using Mersal.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Identity.Infrastructure;

/// <summary>Raised when an EXPLICIT revoke could not be persisted. A6 requires the operator to be told —
/// never a silent success — so this propagates rather than being swallowed into a bool.</summary>
public sealed class RevocationNotPersistedException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// 21.5 — per-identity session and device controls (design 40 §6, 18 §9, adaptation A6).
///
/// Three jobs: list what can currently act as you, cap how many things can, and stop one (or all) of them.
/// The cap revokes the OLDEST rather than refusing the newest: refusing a login because you have too many
/// sessions is indistinguishable, to the person at the desk, from being locked out — and they will phone
/// support instead of closing the laptop they left signed in at home.
/// </summary>
public sealed class SessionService(IdentityStoreDbContext db, TimeProvider clock)
{
    /// <summary>Concurrent sessions per identity (18 §9). Exceeding it revokes the oldest.</summary>
    public const int ConcurrentSessionCap = 5;

    /// <summary>The identity's live sessions, newest first — the order the session list is read in.</summary>
    public async Task<IReadOnlyList<UserSession>> LiveAsync(Guid userId, CancellationToken ct = default) =>
        await db.Sessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

    /// <summary>
    /// Record a new session and enforce the concurrent cap, revoking the oldest live sessions until the
    /// identity is back within it.
    /// </summary>
    public async Task<UserSession> OpenAsync(
        Guid userId, Guid? membershipId, string? userAgent, IPAddress? ip, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var session = new UserSession
        {
            SessionId = Guid.NewGuid(), UserId = userId, MembershipId = membershipId,
            // Truncated rather than rejected: an oversized header is not a reason to fail a valid login.
            UserAgent = Truncate(userAgent, 400), IpAddress = ip,
            CreatedAt = now, LastSeenAt = now,
        };
        db.Sessions.Add(session);

        var live = await db.Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        // `live` excludes the row just added (not yet saved), so the new session counts as one more.
        foreach (var stale in live.Skip(Math.Max(0, ConcurrentSessionCap - 1)))
        {
            stale.RevokedAt = now;
            stale.RevokedBy = "system:concurrent-session-cap";
            stale.RevokeReason = $"exceeded the {ConcurrentSessionCap}-session limit";
        }

        await db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>
    /// Revoke one session on an operator's or owner's instruction.
    ///
    /// A6 — FAILS CLOSED. If this cannot be persisted the caller gets an exception, not a false success:
    /// somebody revoking a session is acting on an off-boarding or a suspected compromise, and letting them
    /// believe the access is gone is worse than telling them the system is degraded. They can escalate; they
    /// cannot un-close an incident they closed on a lie.
    /// </summary>
    public async Task RevokeAsync(Guid sessionId, string revokedBy, string reason, CancellationToken ct = default)
    {
        try
        {
            var session = await db.Sessions.FirstOrDefaultAsync(s => s.SessionId == sessionId, ct)
                ?? throw new RevocationNotPersistedException($"session {sessionId} not found");

            if (session.RevokedAt is not null) return;   // already gone; revoking twice is not an error

            session.RevokedAt = clock.GetUtcNow();
            session.RevokedBy = revokedBy;
            session.RevokeReason = reason;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not RevocationNotPersistedException)
        {
            RevocationDegradation.OnStoreFailure(RevocationOperation.ExplicitRevoke);
            throw new RevocationNotPersistedException(
                "the revocation could not be persisted — the session may still be active", ex);
        }
    }

    /// <summary>Revoke every live session for an identity. Same fail-closed rule.</summary>
    public async Task<int> RevokeAllAsync(
        Guid userId, string revokedBy, string reason, CancellationToken ct = default)
    {
        try
        {
            var live = await db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null).ToListAsync(ct);
            var now = clock.GetUtcNow();
            foreach (var s in live)
            {
                s.RevokedAt = now;
                s.RevokedBy = revokedBy;
                s.RevokeReason = reason;
            }
            await db.SaveChangesAsync(ct);
            return live.Count;
        }
        catch (Exception ex)
        {
            RevocationDegradation.OnStoreFailure(RevocationOperation.ExplicitRevoke);
            throw new RevocationNotPersistedException(
                "the revocations could not be persisted — sessions may still be active", ex);
        }
    }

    /// <summary>
    /// Whether a session is still live, for the refresh path.
    ///
    /// A6 — FAILS OPEN. A revocation-store outage must not log out every clinician on the platform
    /// mid-shift; the exposure is bounded by the access-token TTL and the counter raises the alarm. This is
    /// the exact inverse of <see cref="RevokeAsync"/>, and deliberately so.
    /// </summary>
    public async Task<bool> IsLiveAsync(Guid sessionId, CancellationToken ct = default)
    {
        try
        {
            var revokedAt = await db.Sessions.AsNoTracking()
                .Where(s => s.SessionId == sessionId)
                .Select(s => s.RevokedAt)
                .FirstOrDefaultAsync(ct);
            return revokedAt is null;
        }
        catch
        {
            return RevocationDegradation.OnStoreFailure(RevocationOperation.RefreshCheck)
                == DegradationAction.FailOpen;
        }
    }

    /// <summary>
    /// Record a sign-in attempt. Called for FAILURES as well as successes — a history that only contains
    /// the successes cannot show anyone that their account is being attacked.
    /// </summary>
    public async Task RecordAttemptAsync(
        Guid? userId, string usernameTried, bool succeeded, string? failureReason,
        string? userAgent, IPAddress? ip, CancellationToken ct = default)
    {
        db.LoginAttempts.Add(new LoginAttempt
        {
            UserId = userId,
            UsernameTried = Truncate(usernameTried, 256) ?? "",
            Succeeded = succeeded,
            FailureReason = failureReason,
            UserAgent = Truncate(userAgent, 400),
            IpAddress = ip,
            AttemptedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>An identity's recent sign-in attempts, newest first.</summary>
    public async Task<IReadOnlyList<LoginAttempt>> RecentAttemptsAsync(
        Guid userId, int take = 50, CancellationToken ct = default) =>
        await db.LoginAttempts.AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.AttemptedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
