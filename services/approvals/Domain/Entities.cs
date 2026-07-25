namespace Mersal.Approvals.Domain;

// Medical-approvals domain (22-data-dictionary §9, 23-state-machines §5). Canonical enums used EXACTLY.

/// <summary>Where the authorization request originated (23 §5 / 24-sequence-diagrams). A gated investigation
/// order line or a gated prescription routes here automatically; a reviewer may also create one manually.</summary>
public enum AuthSource { OrderLine, Prescription, Manual }

public enum AuthPriority { Routine, Urgent, Emergency }

/// <summary>Authorization lifecycle (23 §5): Draft → Submitted → UnderReview →
/// (Approved | PartiallyApproved | Rejected | InfoRequested); plus Overridden, EmergencyApproved, Expired.
/// Values are used EXACTLY (stored as text, CHECK-constrained in the DB).</summary>
public enum AuthStatus
{
    Draft, Submitted, UnderReview, Approved, PartiallyApproved, Rejected, InfoRequested,
    Overridden, EmergencyApproved, Expired,
}

/// <summary>A single recorded decision type on the append-only <c>authorization_decision</c> ledger.</summary>
public enum AuthDecision
{
    Approved, PartiallyApproved, Rejected, InfoRequested, Overridden, EmergencyApproved,
}

/// <summary>The authorization aggregate (append-safe): the request, its reviewer assignment, SLA/TAT tracking and
/// current status. Decisions are NEVER mutated in place — each lands as an immutable
/// <see cref="AuthorizationDecision"/> row; the aggregate's <see cref="Status"/> is the projection of the latest.</summary>
public sealed class Authorization
{
    public Guid AuthorizationId { get; set; }
    public string AuthNo { get; set; } = default!;              // AUTH-YYYY-NNNNNN
    public Guid BeneficiaryId { get; set; }
    public AuthSource Source { get; set; }
    public string? SourceRef { get; set; }                      // originating order-line / prescription id
    public Guid? RequestingProviderId { get; set; }             // null for manual authorizations
    public string ServiceCodes { get; set; } = "[]";            // jsonb array of requested service codes
    public string RequestedScope { get; set; } = "{}";          // jsonb — the itemized requested scope
    public AuthPriority Priority { get; set; } = AuthPriority.Routine;
    public AuthStatus Status { get; set; } = AuthStatus.Submitted;
    public Guid? AssignedReviewerId { get; set; }
    public DateTimeOffset? SlaDueAt { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public int? TatSeconds { get; set; }                        // decided_at − submitted_at, persisted for reporting
    public bool SlaBreached { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint RowVersion { get; set; }                        // xmin — optimistic-concurrency token (two reviewers race)
    public List<AuthorizationDecision> Decisions { get; set; } = [];
}

/// <summary>APPEND-ONLY decision record (23 §5, 19-audit-strategy). One immutable row per decision — never updated
/// or deleted (DB trigger + no UPDATE/DELETE grant); corrections are new rows. Carries the reviewer, timestamp,
/// decision, mandatory-where-applicable rationale, the itemized approved scope (for partial), and the break-glass
/// justification for emergency/override/manual paths.</summary>
public sealed class AuthorizationDecision
{
    public Guid DecisionId { get; set; }
    public Guid AuthorizationId { get; set; }
    public AuthDecision Decision { get; set; }
    public Guid ReviewerId { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
    public string? Rationale { get; set; }
    public string? ApprovedScope { get; set; }                  // jsonb — required for PartiallyApproved
    public bool BreakGlass { get; set; }
    public string? Justification { get; set; }                  // required when BreakGlass
    public string? CorrelationId { get; set; }
}

/// <summary>Business-key formatter for authorizations (0A §3, AUTH-*). The codebase uses a 6-digit zero-padded
/// per-year sequence (like ORD-/RX-/MRS-M-); the design's <c>AUTH-YYYY-XXXX</c> shorthand is realized as
/// <c>AUTH-YYYY-NNNNNN</c> for consistency across business keys.</summary>
public static class AuthNo
{
    public static string Format(int year, int sequence) => $"AUTH-{year:D4}-{sequence:D6}";
}
