namespace Mersal.Approvals.Domain;

/// <summary>Why a partial-approval scope is invalid (phase 7.2). A partial approval must name a NON-EMPTY, STRICT
/// subset of the requested codes — empty is not a decision, and a set equal to the full request is a full approval,
/// not a partial one; codes outside the request cannot be approved.</summary>
public enum PartialScopeError { None, Empty, NotSubset, EqualsFull }

/// <summary>Pure decision guards (23-state-machines §5, 19-audit-strategy): mandatory rationale on reject /
/// request-info, mandatory justification on break-glass, and the partial-approval scope check. No I/O.</summary>
public static class DecisionRules
{
    public static bool IsBlank(string? s) => string.IsNullOrWhiteSpace(s);

    /// <summary>Validate a partial-approval scope against the full requested set (order-insensitive, de-duplicated).</summary>
    public static PartialScopeError ValidatePartialScope(IReadOnlyCollection<string> requested, IReadOnlyCollection<string> approved)
    {
        var full = new HashSet<string>(requested, StringComparer.Ordinal);
        var sub = new HashSet<string>(approved, StringComparer.Ordinal);
        if (sub.Count == 0) return PartialScopeError.Empty;
        if (!sub.IsSubsetOf(full)) return PartialScopeError.NotSubset;
        if (sub.SetEquals(full)) return PartialScopeError.EqualsFull;
        return PartialScopeError.None;
    }

    /// <summary>TAT in whole seconds from submission to decision (persisted for reporting).</summary>
    public static int TatSeconds(DateTimeOffset submittedAt, DateTimeOffset decidedAt) =>
        (int)Math.Max(0, (decidedAt - submittedAt).TotalSeconds);

    /// <summary>An SLA breach = decided after the (assign-time) due instant, when a due instant was set.</summary>
    public static bool SlaBreached(DateTimeOffset? slaDueAt, DateTimeOffset decidedAt) =>
        slaDueAt is not null && decidedAt > slaDueAt.Value;
}
