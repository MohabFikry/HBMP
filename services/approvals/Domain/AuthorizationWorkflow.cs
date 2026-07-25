namespace Mersal.Approvals.Domain;

/// <summary>The canonical Authorization transition table (23-state-machines §5) as an explicit state machine with
/// guards. Illegal transitions are rejected (the API maps that to RFC7807 409). Also owns the SLA-due computation
/// (priority-based) and the decision→status mapping so every path agrees on the one lifecycle.</summary>
public static class AuthorizationWorkflow
{
    // Allowed target states from each state (23 §5 transition table).
    private static readonly IReadOnlyDictionary<AuthStatus, AuthStatus[]> Allowed = new Dictionary<AuthStatus, AuthStatus[]>
    {
        [AuthStatus.Draft] = [AuthStatus.Submitted],
        [AuthStatus.Submitted] = [AuthStatus.UnderReview, AuthStatus.EmergencyApproved],
        [AuthStatus.UnderReview] = [AuthStatus.Approved, AuthStatus.PartiallyApproved, AuthStatus.Rejected, AuthStatus.InfoRequested],
        [AuthStatus.InfoRequested] = [AuthStatus.UnderReview],
        [AuthStatus.Rejected] = [AuthStatus.Overridden],
        [AuthStatus.Approved] = [AuthStatus.Expired],
        [AuthStatus.PartiallyApproved] = [AuthStatus.Expired],
        [AuthStatus.EmergencyApproved] = [AuthStatus.Expired],
        [AuthStatus.Overridden] = [AuthStatus.Expired],
    };

    /// <summary>True when <paramref name="from"/> → <paramref name="to"/> is a legal transition.</summary>
    public static bool CanTransition(AuthStatus from, AuthStatus to) =>
        Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    /// <summary>The status a given decision drives the aggregate to.</summary>
    public static AuthStatus ResultOf(AuthDecision decision) => decision switch
    {
        AuthDecision.Approved => AuthStatus.Approved,
        AuthDecision.PartiallyApproved => AuthStatus.PartiallyApproved,
        AuthDecision.Rejected => AuthStatus.Rejected,
        AuthDecision.InfoRequested => AuthStatus.InfoRequested,
        AuthDecision.Overridden => AuthStatus.Overridden,
        AuthDecision.EmergencyApproved => AuthStatus.EmergencyApproved,
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, null),
    };

    /// <summary>A decision reached from a still-open review state releases (or partially releases) downstream; a
    /// terminal decision is one the linked order/prescription gate reacts to.</summary>
    public static bool ReleasesDownstream(AuthDecision d) =>
        d is AuthDecision.Approved or AuthDecision.PartiallyApproved
          or AuthDecision.EmergencyApproved or AuthDecision.Overridden;

    /// <summary>SLA due time from priority (policy default; overridable via config in the service). The timer
    /// starts when a reviewer picks the case up (UnderReview), per 23 §5.</summary>
    public static DateTimeOffset SlaDue(AuthPriority priority, DateTimeOffset from) => priority switch
    {
        AuthPriority.Emergency => from.AddHours(1),
        AuthPriority.Urgent => from.AddHours(4),
        _ => from.AddHours(48),
    };
}
