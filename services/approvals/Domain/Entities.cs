namespace Mersal.Approvals.Domain;

// Medical-approvals domain (22-data-dictionary §9, 23-state-machines §5). Canonical enums used EXACTLY.

/// <summary>Where the authorization request originated (23 §5 / 24-sequence-diagrams). A gated investigation
/// order line or a gated prescription routes here automatically; a reviewer may also create one manually.</summary>
public enum AuthSource
{
    OrderLine,
    Prescription,
    Manual,
    /// <summary>
    /// A pharmacist or a lab/imaging technician asking for an EXPIRED prescription or investigation order to
    /// be made actionable again.
    ///
    /// <para>It rides the same aggregate as the other three so it lands in the queue the approval team
    /// already works, with the same SLA clock, the same append-only ledger and the same audit trail.
    /// <c>SourceRef</c> is the expired item's id; approving it resets that item's validity to the tenant's
    /// configured period from the moment of the decision.</para>
    /// </summary>
    ValidityExtension,
}

public enum AuthPriority { Routine, Urgent, Emergency }

/// <summary>
/// What KIND of authorization this row is (ADR-0034): a question awaiting an answer, or a record of
/// something already delivered.
/// </summary>
/// <remarks>
/// <para>The two share the aggregate — one number space, one worklist, one audit trail, the argument the
/// 0005 migration made when validity extensions became a fourth <see cref="AuthSource"/> rather than a
/// parallel table. They do NOT share the lifecycle: a <see cref="Fulfilment"/> is born
/// <see cref="AuthStatus.Issued"/> and <c>AuthorizationWorkflow</c> admits no transition into or out of that
/// status, so settled work can never be assigned to a reviewer and start an SLA clock on a question nobody
/// asked.</para>
/// <para>Set once, at creation, and never updated.</para>
/// </remarks>
public enum AuthKind
{
    /// <summary>A request for a decision — the reviewer inbox.</summary>
    Review,

    /// <summary>What a counter actually handed over, or a bench actually performed.</summary>
    Fulfilment,
}

/// <summary>Authorization lifecycle (23 §5): Draft → Submitted → UnderReview →
/// (Approved | PartiallyApproved | Rejected | InfoRequested); plus Overridden, EmergencyApproved, Expired.
/// Values are used EXACTLY (stored as text, CHECK-constrained in the DB).
/// <para><see cref="Issued"/> is outside that machine entirely — see <see cref="AuthKind.Fulfilment"/>.</para></summary>
public enum AuthStatus
{
    Draft, Submitted, UnderReview, Approved, PartiallyApproved, Rejected, InfoRequested,
    Overridden, EmergencyApproved, Expired,

    /// <summary>
    /// A fulfilment authorization: the medicine is in the patient's hand, the scan has been performed.
    /// TERMINAL and unreachable — no transition targets it and none leaves it.
    /// </summary>
    Issued,
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
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public string AuthNo { get; set; } = default!;              // AUTH-YYYY-NNNNNN
    public Guid BeneficiaryId { get; set; }
    /// <summary>Review request or fulfilment record (ADR-0034). Set at creation; never updated.</summary>
    public AuthKind Kind { get; set; } = AuthKind.Review;
    public AuthSource Source { get; set; }
    public string? SourceRef { get; set; }                      // originating order-line / prescription id
    /// <summary>The visit the authorized thing was ordered in (ADR-0031). NULL for a manual authorization —
    /// a reviewer raising one has no encounter in hand — and for anything ingested before the seam carried it.</summary>
    public Guid? EncounterId { get; set; }
    public Guid? RequestingProviderId { get; set; }             // null for manual authorizations
    public string ServiceCodes { get; set; } = "[]";            // jsonb array of requested service codes
    public string RequestedScope { get; set; } = "{}";          // jsonb — the itemized requested scope
    public AuthPriority Priority { get; set; } = AuthPriority.Routine;
    public AuthStatus Status { get; set; } = AuthStatus.Submitted;
    public Guid? AssignedReviewerId { get; set; }
    /// <summary>
    /// Where the engine routed this (ADR-0035 §5.4).
    /// </summary>
    /// <remarks>
    /// NULL means no rule has looked at it yet — a different fact from "a rule sent it to the default queue",
    /// and the second is a decision worth being able to see. Rows written before the engine existed keep NULL
    /// rather than being backfilled with a claim nobody made.
    /// </remarks>
    public string? RoutedQueue { get; set; }
    /// <summary>Which rule chose the queue, so the routing can be explained without re-deriving it against a
    /// rule set that may since have moved on.</summary>
    public Guid? RoutedByRule { get; set; }
    public DateTimeOffset? SlaDueAt { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public int? TatSeconds { get; set; }                        // decided_at − submitted_at, persisted for reporting
    public bool SlaBreached { get; set; }
    public bool RetrospectiveReviewRequired { get; set; }       // set by a break-glass decision (7.3)
    public bool RetrospectiveReviewed { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint RowVersion { get; set; }                        // xmin — optimistic-concurrency token (two reviewers race)
    public List<AuthorizationDecision> Decisions { get; set; } = [];
    /// <summary>What was actually delivered against this authorization. Empty for a review request.</summary>
    public List<AuthorizationItem> Items { get; set; } = [];
}

/// <summary>
/// One delivered thing on a fulfilment authorization: a dispense of a medicine, a performed panel.
/// </summary>
/// <remarks>
/// <para><b>Ordered and fulfilled are two fields, not one field plus a flag.</b> A substitution is not an
/// edit to what the prescriber decided. Writing the delivered molecule into the field that held the
/// prescribed one would destroy the record of the clinical decision — which is the fact a later reviewer
/// most needs, and the reason the prescription itself is never written to by this path.</para>
/// <para><see cref="FulfilmentRef"/> is the dispense / order-fulfillment id and is UNIQUE per tenant. It is
/// what makes a redelivered event under a *different* event id harmless: at-least-once delivery is guarded
/// once by the processed-event ledger and once here, because only the second guard survives a replay the
/// first has forgotten.</para>
/// </remarks>
public sealed class AuthorizationItem
{
    public Guid ItemId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid AuthorizationId { get; set; }
    /// <summary>The prescription line / order line this came from.</summary>
    public Guid? SourceLineId { get; set; }
    /// <summary>The dispense / fulfillment id — the idempotency anchor. UNIQUE per tenant.</summary>
    public string FulfilmentRef { get; set; } = default!;
    /// <summary>What the clinician wrote.</summary>
    public string OrderedCode { get; set; } = default!;
    public string? OrderedLabel { get; set; }
    /// <summary>What was actually handed over or performed. Equal to <see cref="OrderedCode"/> when nothing
    /// was substituted — the common case, and stored rather than inferred so the row reads on its own.</summary>
    public string FulfilledCode { get; set; } = default!;
    public string? FulfilledLabel { get; set; }
    public decimal Quantity { get; set; }
    /// <summary>Required when the codes differ; null otherwise. See ADR-0034 Decision 3 on why this one
    /// non-clinical sentence is allowed into a projection that carries no clinical payload.</summary>
    public string? SubstitutionReason { get; set; }
    public DateTimeOffset FulfilledAt { get; set; }

    /// <summary>True when what was handed over is not what was written.</summary>
    public bool Substituted => !string.Equals(OrderedCode, FulfilledCode, StringComparison.Ordinal);
}

/// <summary>APPEND-ONLY decision record (23 §5, 19-audit-strategy). One immutable row per decision — never updated
/// or deleted (DB trigger + no UPDATE/DELETE grant); corrections are new rows. Carries the reviewer, timestamp,
/// decision, mandatory-where-applicable rationale, the itemized approved scope (for partial), and the break-glass
/// justification for emergency/override/manual paths.</summary>
public sealed class AuthorizationDecision
{
    public Guid DecisionId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid AuthorizationId { get; set; }
    public AuthDecision Decision { get; set; }
    /// <summary>
    /// The person who decided, or NULL when the engine did.
    /// </summary>
    /// <remarks>
    /// Nullable since ADR-0035 §5.3, paired with <see cref="DecidedByRule"/> under a database CHECK that
    /// exactly one is set. Attributing a machine decision to a human is a falsified audit record, and this
    /// ledger is hash-chained precisely so that cannot happen — a sentinel Guid meaning "the system" would be
    /// worse, because it reads as a person's id everywhere it is joined.
    /// </remarks>
    public Guid? ReviewerId { get; set; }
    /// <summary>The rule that decided, or NULL when a person did. Exactly one of the two is always set.</summary>
    public Guid? DecidedByRule { get; set; }
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
