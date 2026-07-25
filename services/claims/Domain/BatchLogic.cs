namespace Mersal.Claims.Domain;

/// <summary>The batch lifecycle transition table (23 §9). Illegal transitions are rejected at the service with 409;
/// the guards (≥1 claim to review, every line decided to Decide, reason to cancel) are applied by the caller.</summary>
public static class BatchTransitions
{
    private static readonly HashSet<(BatchStatus, BatchStatus)> Allowed =
    [
        (BatchStatus.Open, BatchStatus.UnderReview),
        (BatchStatus.Open, BatchStatus.Cancelled),
        (BatchStatus.UnderReview, BatchStatus.Open),          // reopen (no decisions yet)
        (BatchStatus.UnderReview, BatchStatus.Decided),        // every line decided
        (BatchStatus.UnderReview, BatchStatus.Cancelled),      // reason mandatory
        (BatchStatus.Decided, BatchStatus.SettlementIssued),
        (BatchStatus.SettlementIssued, BatchStatus.Closed),
    ];

    public static bool CanTransition(BatchStatus from, BatchStatus to) => Allowed.Contains((from, to));

    /// <summary>True while a batch is live enough to constrain claim membership (a claim may be in only one such).</summary>
    public static bool IsOpenOrUnderReview(BatchStatus s) => s is BatchStatus.Open or BatchStatus.UnderReview;

    /// <summary>True once a batch's membership + selection is locked (nothing may be added/removed w/o exception path).</summary>
    public static bool IsMembershipLocked(BatchStatus s) =>
        s is BatchStatus.Decided or BatchStatus.SettlementIssued or BatchStatus.Closed;
}

/// <summary>Rollup totals over a batch's member claim lines (22 §10A.5). Pure and deterministic so it can be unit
/// tested without a database and recomputed identically on every membership/decision change (frozen at
/// SettlementIssued). Adjustments (10b.7) net into <see cref="Adjusted"/>; until then it is zero.</summary>
public readonly record struct BatchRollup(
    decimal Claimed, decimal Priced, decimal Approved, decimal Adjusted, decimal Denied, decimal NetPayable)
{
    public static BatchRollup Compute(IEnumerable<ClaimLine> lines, decimal adjusted = 0m)
    {
        decimal claimed = 0, priced = 0, approved = 0, denied = 0;
        foreach (var l in lines)
        {
            if (l.Status == ClaimLineStatus.Void) continue;
            claimed += l.BilledAmount;
            priced += l.ContractPrice ?? 0m;
            switch (l.Status)
            {
                case ClaimLineStatus.Approved:
                case ClaimLineStatus.PartiallyApproved:
                case ClaimLineStatus.Adjusted:
                    approved += l.AllowedAmount ?? 0m;
                    break;
                case ClaimLineStatus.Denied:
                    denied += l.BilledAmount;
                    break;
            }
        }
        return new BatchRollup(claimed, priced, approved, adjusted, denied, approved + adjusted);
    }
}
