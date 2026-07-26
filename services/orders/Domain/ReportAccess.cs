namespace Mersal.Orders.Domain;

// Phase 14.7 — sensitive-result gating + the justified release-request workflow (design 37 §6). THE PRIVACY
// HEART: for a non-Standard result, FULL CONTENT is readable ONLY by the authoring/ordering doctor or the
// holder of an active, single-result, time-boxed grant. Everyone else — including the medical approval team
// and case managers — gets EXISTENCE METADATA ONLY. This deliberately overrides the approval team's standing
// EMR oversight for sensitive results.

public enum PurposeCode { ContinuityOfCare, AuthorizationDecision, ClinicalReview, Complaint, Legal, Other }

public enum ReportAccessStatus { Requested, UnderReview, InfoRequested, Approved, Denied, Expired, Revoked }

/// <summary>What a caller may see for a result (design 37 §6).</summary>
public enum ResultDisclosure { Full, ExistenceOnly }

public sealed class ReportAccessRequest
{
    public Guid RequestId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid OrderId { get; set; }
    public Guid OrderLineId { get; set; }
    public Guid BeneficiaryId { get; set; }
    public string RequestedBy { get; set; } = default!;
    public string? RequestedForRole { get; set; }
    public PurposeCode PurposeCode { get; set; }
    public string Justification { get; set; } = default!;
    public int RequestedTtlHours { get; set; }
    public ReportAccessStatus Status { get; set; } = ReportAccessStatus.Requested;
    public string? DecidedBy { get; set; }
    public string? DecidedByRole { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ReportAccessGrant
{
    public Guid GrantId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid RequestId { get; set; }
    public string GranteeUserId { get; set; } = default!;
    public Guid OrderLineId { get; set; }             // single-result scope
    public PurposeCode PurposeCode { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }

    public bool IsActiveAt(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;
}

/// <summary>
/// Phase 18.A4 — the report-access request lifecycle as declared in 23 §11:
/// <c>Requested → UnderReview → (InfoRequested ⇄ UnderReview) → (Approved | Denied)</c>, and an approved
/// request's grant ends <c>Expired</c> or <c>Revoked</c>.
///
/// Three of those states were unreachable before this type existed: nothing routed a request to
/// <c>UnderReview</c>, a request that entered <c>InfoRequested</c> had no supply-info path back, and the
/// request itself never followed its grant to <c>Expired</c>/<c>Revoked</c> — so the request table and
/// the grant table could disagree about whether access was still live.
/// </summary>
public static class ReportAccessWorkflow
{
    private static readonly Dictionary<ReportAccessStatus, HashSet<ReportAccessStatus>> Allowed = new()
    {
        [ReportAccessStatus.Requested] = [ReportAccessStatus.UnderReview],
        [ReportAccessStatus.UnderReview] = [ReportAccessStatus.InfoRequested, ReportAccessStatus.Approved, ReportAccessStatus.Denied],
        [ReportAccessStatus.InfoRequested] = [ReportAccessStatus.UnderReview],
        [ReportAccessStatus.Approved] = [ReportAccessStatus.Expired, ReportAccessStatus.Revoked],
        [ReportAccessStatus.Denied] = [],
        [ReportAccessStatus.Expired] = [],
        [ReportAccessStatus.Revoked] = [],
    };

    /// <summary>Terminal states — a request here is finished and never moves again (23 §11).</summary>
    public static bool IsTerminal(ReportAccessStatus status) =>
        status is ReportAccessStatus.Denied or ReportAccessStatus.Expired or ReportAccessStatus.Revoked;

    /// <summary>States a decider may act on. <c>Requested</c> is included so a decider who acts before the
    /// routing step is not blocked — the service records the implicit pick-up as UnderReview first.</summary>
    public static bool IsDecidable(ReportAccessStatus status) =>
        status is ReportAccessStatus.Requested or ReportAccessStatus.UnderReview or ReportAccessStatus.InfoRequested;

    public static bool CanTransition(ReportAccessStatus from, ReportAccessStatus to) =>
        Allowed.TryGetValue(from, out var set) && set.Contains(to);

    /// <summary>Validate a requested move; null when legal, else a short reason for the 409 + the
    /// <c>TransitionDenied</c> audit event.</summary>
    public static string? Validate(ReportAccessStatus from, ReportAccessStatus to)
    {
        if (from == to) return $"already in status {to}";
        if (IsTerminal(from)) return $"{from} is terminal";
        return CanTransition(from, to) ? null : $"illegal transition {from} → {to}";
    }
}

/// <summary>Pure gating + release rules (design 37 §6). No I/O — the service resolves author/grant facts and
/// these decide disclosure, request validity, decider eligibility and grant TTLs.</summary>
public static class SensitiveResultGate
{
    /// <summary>Disclosure decision. Standard results follow the ordinary min-necessary rules (Full here);
    /// a non-Standard result is Full ONLY for the author or an active-grant holder — else ExistenceOnly.</summary>
    public static ResultDisclosure Decide(SensitivityLevel level, bool isAuthor, bool hasActiveGrant) =>
        level == SensitivityLevel.Standard || isAuthor || hasActiveGrant
            ? ResultDisclosure.Full
            : ResultDisclosure.ExistenceOnly;

    /// <summary>Grant TTL: 72h for Sensitive, 24h for HighlySensitive (design 37 §6, configurable). This is
    /// both the default AND the policy maximum — see <see cref="EffectiveTtlHours"/>.</summary>
    public static int DefaultTtlHours(SensitivityLevel level) => level == SensitivityLevel.HighlySensitive ? 24 : 72;

    /// <summary>
    /// 18.A4 — the TTL a grant is actually issued with. The caller may ask for LESS than the policy
    /// maximum but never more: the decision endpoint used to pass a caller-supplied <c>TtlHours</c>
    /// straight through, so a decider (or anything posting on their behalf) could mint a year-long grant
    /// over a HighlySensitive result that policy caps at 24 hours. A non-positive request falls back to
    /// the default. Grants are never extended — a longer need is a new request (23 §11).
    /// </summary>
    public static int EffectiveTtlHours(SensitivityLevel level, int? requestedHours)
    {
        var max = DefaultTtlHours(level);
        return requestedHours is not { } h || h <= 0 ? max : Math.Min(h, max);
    }

    /// <summary>A request must carry a purpose and a non-blank justification (else 422).</summary>
    public static bool IsRequestValid(string? justification) => !string.IsNullOrWhiteSpace(justification);

    /// <summary>Deciders: the AUTHORING/ORDERING doctor OR a Medical Director (so care isn't blocked when the
    /// author is unavailable). A Medical Director decision is flagged + extra-audited by the caller.</summary>
    public static bool CanDecide(bool isAuthor, IReadOnlySet<string> roles) =>
        isAuthor || roles.Contains("medical_director");
}
