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

    /// <summary>Grant TTL: 72h for Sensitive, 24h for HighlySensitive (design 37 §6, configurable).</summary>
    public static int DefaultTtlHours(SensitivityLevel level) => level == SensitivityLevel.HighlySensitive ? 24 : 72;

    /// <summary>A request must carry a purpose and a non-blank justification (else 422).</summary>
    public static bool IsRequestValid(string? justification) => !string.IsNullOrWhiteSpace(justification);

    /// <summary>Deciders: the AUTHORING/ORDERING doctor OR a Medical Director (so care isn't blocked when the
    /// author is unavailable). A Medical Director decision is flagged + extra-audited by the caller.</summary>
    public static bool CanDecide(bool isAuthor, IReadOnlySet<string> roles) =>
        isAuthor || roles.Contains("medical_director");
}
