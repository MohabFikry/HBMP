using System.Diagnostics.Metrics;

namespace Mersal.Authz;

/// <summary>What the caller was trying to do when the revocation store failed.</summary>
public enum RevocationOperation
{
    /// <summary>A refresh-time check: "has this session been revoked?" Happens on every token exchange.</summary>
    RefreshCheck,

    /// <summary>An operator explicitly revoking a session or a family. Happens because a human decided to.</summary>
    ExplicitRevoke,
}

/// <summary>What the caller must do.</summary>
public enum DegradationAction
{
    /// <summary>Proceed. Bounded exposure, alarmed.</summary>
    FailOpen,

    /// <summary>Refuse and tell the operator. Never report success.</summary>
    FailClosed,
}

/// <summary>
/// A6 — bounded, alarmed revocation fail-open (design 40 §0 A6, §6).
///
/// The two directions are opposite ON PURPOSE, and getting them the same way round is the mistake:
///
///   • A REFRESH-TIME CHECK fails OPEN. If the revocation store is down and every refresh is refused, an
///     infrastructure blip logs out every clinician on the platform mid-shift. The exposure from failing
///     open is bounded — a revoked session survives at most one more refresh cycle, and stateless access
///     tokens expire on their own short TTL regardless. Losing the whole clinic's session is the larger
///     harm, so it proceeds AND raises an alarm; the runbook states the bound.
///
///   • An EXPLICIT REVOKE fails CLOSED. An operator revoking a session is acting on a decision — an
///     off-boarding, a suspected compromise. Reporting success for a revocation that was never persisted
///     is worse than any outage: they will believe the access is gone, close the incident, and stop
///     looking. So it returns an error and they can escalate.
///
/// Stateless access-token validation never consults this at all (A6): it is signature + expiry only, so a
/// revocation-store outage cannot break request authorization.
/// </summary>
public static class RevocationDegradation
{
    public const string MeterName = "Mersal.Authz.Revocation";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Counter the Prometheus alert rule fires on. Tagged by operation so "we are failing open"
    /// and "an operator could not revoke" are separately alertable — they need different responses.</summary>
    public static readonly Counter<long> Failures = Meter.CreateCounter<long>(
        "hbmp.authz.revocation_store_failures",
        unit: "{failure}",
        description: "Revocation-store errors, tagged by operation and the degradation taken (A6).");

    /// <summary>Decide what to do when the revocation store is unavailable, and record it.</summary>
    public static DegradationAction OnStoreFailure(RevocationOperation operation)
    {
        var action = operation switch
        {
            RevocationOperation.RefreshCheck => DegradationAction.FailOpen,
            RevocationOperation.ExplicitRevoke => DegradationAction.FailClosed,

            // A new operation must not silently inherit the permissive branch. Anything unrecognised is
            // treated as the safe direction, and the counter still fires so it is visible.
            _ => DegradationAction.FailClosed,
        };

        Failures.Add(1,
            new KeyValuePair<string, object?>("operation", operation.ToString()),
            new KeyValuePair<string, object?>("action", action.ToString()));

        return action;
    }

    /// <summary>
    /// How long a revoked session can survive a fail-open, given the access-token TTL. This is the bound the
    /// runbook has to state: "revocation may lag by up to X" is an answerable question during an incident,
    /// and "we are not sure" is not.
    /// </summary>
    public static TimeSpan ExposureBound(TimeSpan accessTokenTtl) => accessTokenTtl;
}
