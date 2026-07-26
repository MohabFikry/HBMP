namespace Mersal.Claims.Domain;

/// <summary>An append-only Claims Officer decision on one line (22 §10A.3). Never edited or deleted — a changed
/// outcome is a NEW row (DB trigger + no UPDATE/DELETE grant enforce it). SoD: <see cref="DecidedBy"/> is never the
/// claim's originator and never provider-affiliated. Dual control: a high-value decision is recorded
/// <see cref="PendingSecondApproval"/> and takes effect only when a second, distinct approver adds a confirming row
/// (<see cref="ConfirmsDecisionId"/>).</summary>
public sealed class ClaimDecision
{
    public Guid DecisionId { get; set; }
    public Guid ClaimLineId { get; set; }
    public Guid ClaimId { get; set; }
    public string TenantId { get; set; } = default!;
    public ClaimDecisionKind Decision { get; set; }
    public decimal? AllowedAmount { get; set; }
    public List<string> ReasonCodes { get; set; } = [];
    public string? Rationale { get; set; }
    public string DecidedBy { get; set; } = default!;
    public DateTimeOffset DecidedAt { get; set; }
    public string? RuleVersion { get; set; }
    public string CorrelationId { get; set; } = "";
    public bool PendingSecondApproval { get; set; }
    public Guid? ConfirmsDecisionId { get; set; }
    public string? IdempotencyKey { get; set; }
}

/// <summary>Pure rules for line decisions: what a decision requires (mandatory reason/rationale), how a line's status
/// and allowed amount follow from a decision, and how line statuses roll up to a claim status (23 §7/§8).</summary>
public static class DecisionRules
{
    /// <summary>Reason code(s) are mandatory for Deny/PartiallyApprove; rationale is mandatory for Deny/Adjust (and
    /// for any override — the caller flags overrides). Returns null when valid, else a short error token.</summary>
    public static string? Validate(ClaimDecisionKind kind, decimal? allowed, IReadOnlyCollection<string> reasonCodes,
        string? rationale, decimal billed, decimal? contractPrice, bool isOverride)
    {
        var needsReason = kind is ClaimDecisionKind.Deny or ClaimDecisionKind.PartiallyApprove;
        var needsRationale = kind is ClaimDecisionKind.Deny or ClaimDecisionKind.Adjust || isOverride;

        if (needsReason && reasonCodes.Count == 0) return "reason-code-required";
        if (reasonCodes.Any(c => !ReasonCodes.IsKnown(c))) return "unknown-reason-code";
        if (needsRationale && string.IsNullOrWhiteSpace(rationale)) return "rationale-required";

        if (kind == ClaimDecisionKind.PartiallyApprove)
        {
            if (allowed is not { } a || a <= 0) return "allowed-amount-required";
            var cap = Math.Max(billed, contractPrice ?? 0m);
            if (a > cap) return "allowed-exceeds-cap";
        }
        if (kind == ClaimDecisionKind.Approve && allowed is { } ap)
        {
            var cap = Math.Max(billed, contractPrice ?? 0m);
            if (ap > cap) return "allowed-exceeds-cap";
        }
        return null;
    }

    /// <summary>The line status + allowed amount a decision produces. RequestInfo/RouteToClinical do not close the
    /// line (it stays Pending) — they change the CLAIM status; those return null here.</summary>
    public static (ClaimLineStatus Status, decimal Allowed)? Apply(ClaimDecisionKind kind, decimal? allowed,
        decimal billed, decimal? contractPrice) => kind switch
    {
        ClaimDecisionKind.Approve => (ClaimLineStatus.Approved, allowed ?? Math.Min(billed, contractPrice ?? billed)),
        ClaimDecisionKind.PartiallyApprove => (ClaimLineStatus.PartiallyApproved, allowed ?? 0m),
        ClaimDecisionKind.Deny => (ClaimLineStatus.Denied, 0m),
        ClaimDecisionKind.Adjust => (ClaimLineStatus.Adjusted, allowed ?? 0m),
        _ => null, // RequestInfo / RouteToClinical
    };

    /// <summary>Roll line statuses up to the claim status (23 §7). Any Pending line ⇒ still UnderAdjudication.</summary>
    public static ClaimStatus RollUp(IReadOnlyCollection<ClaimLineStatus> lineStatuses)
    {
        var live = lineStatuses.Where(s => s != ClaimLineStatus.Void).ToList();
        if (live.Count == 0 || live.Any(s => s == ClaimLineStatus.Pending)) return ClaimStatus.UnderAdjudication;
        if (live.All(s => s == ClaimLineStatus.Denied)) return ClaimStatus.Denied;
        if (live.All(s => s is ClaimLineStatus.Approved)) return ClaimStatus.Approved;
        return ClaimStatus.PartiallyApproved; // mixed outcomes (some approved/partial/adjusted, some denied)
    }
}
