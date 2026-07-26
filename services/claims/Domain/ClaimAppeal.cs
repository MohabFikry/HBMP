namespace Mersal.Claims.Domain;

/// <summary>Who is appealing a decided claim.</summary>
public enum AppellantType { Provider, Beneficiary }

/// <summary>How an appeal is resolved: re-adjudication of a live claim, or (when the batch is already settled) routed to
/// a compensating adjustment/recovery in a LATER batch — a settled batch is never reopened.</summary>
public enum AppealResolution { ReAdjudication, RoutedToAdjustment }

/// <summary>An appeal of a decided claim/line (36 §6, 23 §7). APPEND-ONLY and parallel to the authorization
/// InfoRequested/resubmit path: the prior <c>claim_decision</c> rows are NEVER edited or hidden — the appeal and its
/// re-decision are new rows linked to the original via <c>appeal_id</c> / <c>original_decision_id</c>. A live claim
/// re-enters UnderAdjudication; a settled one is corrected by adjustment in a later batch, untouched.</summary>
public sealed class ClaimAppeal
{
    public Guid AppealId { get; set; }
    public Guid ClaimId { get; set; }
    public Guid? ClaimLineId { get; set; }
    public string TenantId { get; set; } = default!;
    public AppellantType AppellantType { get; set; }
    /// <summary>Mandatory reason for the appeal (non-clinical).</summary>
    public string Reason { get; set; } = default!;
    /// <summary>Set when Mersal submits on the appellant's behalf.</summary>
    public string? ActingFor { get; set; }
    /// <summary>The original decision being appealed (preserved, never edited).</summary>
    public Guid? OriginalDecisionId { get; set; }
    public AppealResolution Resolution { get; set; }
    public string CreatedBy { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
}
