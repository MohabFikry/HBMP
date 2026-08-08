namespace Mersal.Orders.Domain;

// Investigation-orders domain (22-data-dictionary §7, 23-state-machines §2). Canonical enums used EXACTLY.

/// <summary>
/// 29.1 (design 45 §1) — <see cref="Radiology"/> is the canonical name; <see cref="Imaging"/> is retained
/// ONLY so rows and event payloads written before the switch still parse.
///
/// <para>Two values that mean the same thing is a hazard, so it is bounded on both sides: never write
/// <c>Imaging</c> in new code (<see cref="OrderTypes.Canonical"/> maps it), and the value disappears entirely
/// at the contract step — see docs/runbooks/radiology-rename.md.</para>
/// </summary>
public enum OrderType { Lab, Imaging, Radiology, Procedure }

/// <summary>29.1 — the one place the deprecated <see cref="OrderType.Imaging"/> spelling is resolved.</summary>
public static class OrderTypes
{
    /// <summary>Collapse the legacy spelling onto the canonical one. Apply on every READ of a stored or
    /// transported order type, so the rest of the domain only ever sees <see cref="OrderType.Radiology"/>.</summary>
    public static OrderType Canonical(OrderType type) => type == OrderType.Imaging ? OrderType.Radiology : type;

    /// <summary>Parse a stored/transported value, accepting both spellings, and return the canonical one.
    /// False when the value is not an order type at all — absence is never resolved to a default here,
    /// because guessing an order type routes a beneficiary's benefit to the wrong category.</summary>
    public static bool TryParse(string? value, out OrderType type)
    {
        if (Enum.TryParse(value, ignoreCase: true, out type))
        {
            type = Canonical(type);
            return true;
        }
        return false;
    }
}

public enum CodeSystem { CPT, LOINC, LOCAL }

/// <summary>Order lifecycle (§2): Requested → PendingApproval → (Approved|Rejected) → Active → PartiallyUsed →
/// Completed; plus Expired, Cancelled. Phase 4.2 covers create + routing (up to Active/PendingApproval); the
/// consume path (Active→PartiallyUsed→Completed) is phase 5.</summary>
public enum OrderStatus { Requested, PendingApproval, Approved, Rejected, Active, PartiallyUsed, Completed, Expired, Cancelled }

/// <summary>30.1 — <see cref="Superseded"/> is the state a line enters when it is AMENDED: the row is never
/// mutated, a new version is inserted, and this one steps aside pointing at its successor (design 46 §1).
/// It is a line status only; there is deliberately no head status of the same name — see orders 0013.</summary>
public enum OrderLineStatus { Active, PartiallyUsed, Completed, Cancelled, Superseded }

/// <summary>Clinical sensitivity ladder (phase 14.6, design 37 §5). Pinned from the examination type at order
/// creation so later reclassification cannot retroactively unlock already-restricted data.</summary>
public enum SensitivityLevel { Standard, Sensitive, HighlySensitive }

public sealed class InvestigationOrder
{
    public Guid OrderId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public string OrderNo { get; set; } = default!;      // ORD-YYYY-NNNNNN
    public Guid BeneficiaryId { get; set; }
    public Guid EncounterId { get; set; }
    public Guid OrderingProviderId { get; set; }
    /// <summary>The Mersal branch where the order was raised (phase 14.4). NULL for legacy/external contexts —
    /// branch scoping applies only to branch-bound orders (design 37 §3).</summary>
    public Guid? OrderingBranchId { get; set; }
    public Guid? AuthorizationId { get; set; }
    public OrderType OrderType { get; set; }

    // ---- 31.1 — the OP-Procedure COURSE, at the level it is actually decided (design 45 §2, revised) ------

    /// <summary>
    /// The OP-Procedure KIND (<c>masterdata.procedure_type</c>) — one per ORDER, because it is one clinical
    /// decision. NULL on Lab and Radiology orders.
    /// </summary>
    /// <remarks>
    /// It sat on each LINE until 31.1, which allowed a two-item course to carry two different kinds and two
    /// different session counts — not a course any centre can deliver. Validated on the write path against
    /// EVERY line's CPT section: an unvalidated type field is decorative, and every report built on it is
    /// quietly wrong.
    /// </remarks>
    public string? ProcedureTypeCode { get; set; }

    /// <summary>
    /// The course length in attendances. NULL when the type is not delivered in sessions.
    /// </summary>
    /// <remarks>
    /// NULL is not 1: "this kind has no sessions" and "a one-session course" are different facts, and only
    /// the second should ever render a session count. This is what was REQUESTED — sessions AUTHORISED is
    /// derived from <c>OrderLine.QuantityOrdered</c>, which a partial approval narrows.
    /// </remarks>
    public int? Sessions { get; set; }

    /// <summary>
    /// 29.2b — the provider this order is ROUTED TO for delivery (design 45 §2b). THE row-level ownership
    /// anchor for the external-provider portal.
    ///
    /// <para>Distinct from <c>OrderingProviderId</c> (who asked) and from
    /// <c>order_fulfillment.performing_provider_id</c> (who did it, written after the fact). A queue is work
    /// NOT YET DONE, so neither of those can scope one — which is how the pharmacy queue came to be
    /// network-wide (audit R3).</para>
    ///
    /// <para>NULL for Lab and Radiology orders fulfilled inside Mersal's own clinics. Null means "no external
    /// owner", never "everyone's" — see <see cref="ProviderOwnership"/>.</para>
    /// </summary>
    public Guid? AssignedProviderId { get; set; }

    /// <summary>29.2b — the clinical context the ordering doctor CHOSE to share with the delivering provider
    /// (design 45 §2b). Stored, not resolved at read time: this column IS the record of what was disclosed,
    /// and a live join would let the disclosure drift after the clinician made it.</summary>
    public string? SharedClinicalContext { get; set; }
    public string? SharedContextBy { get; set; }
    public DateTimeOffset? SharedContextAt { get; set; }

    /// <summary>29.2b — the delivering provider's report back to the ordering doctor (design 45 §2b). For a
    /// REFERRAL this is mandatory: an open referral loop — the beneficiary was sent somewhere and nobody ever
    /// learned what happened — is the classic outpatient patient-safety failure.</summary>
    public string? CompletionReport { get; set; }
    public string? CompletionReportedBy { get; set; }
    public DateTimeOffset? CompletionReportedAt { get; set; }
    /// <summary>Pinned sensitivity for the order = max of its lines (phase 14.6). Denormalized so read-time
    /// gating (14.7) never needs a cross-service join. Pre-existing rows default to Standard.</summary>
    public SensitivityLevel SensitivityLevel { get; set; } = SensitivityLevel.Standard;
    public OrderStatus Status { get; set; } = OrderStatus.Requested;
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>The approvals authorization that put this order back in date, if any. Doubles as the
    /// idempotency key for the apply — a retried callback for the same authorization grants no second period.</summary>
    public Guid? ValidityExtendedBy { get; set; }
    public DateTimeOffset? ValidityExtendedAt { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? CreatedBy { get; set; }
    public uint RowVersion { get; set; }                 // xmin optimistic-concurrency token
    public List<OrderLine> Lines { get; set; } = [];
}

public sealed class OrderLine
{
    public Guid OrderLineId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid OrderId { get; set; }
    public CodeSystem CodeSystem { get; set; }
    public string Code { get; set; } = default!;
    public string? Description { get; set; }
    /// <summary>The classified examination type this line represents (phase 14.6). NULL for legacy lines.</summary>
    public Guid? ExaminationTypeId { get; set; }
    /// <summary>Pinned sensitivity from the examination type (phase 14.6). Default Standard; results inherit it.</summary>
    public SensitivityLevel SensitivityLevel { get; set; } = SensitivityLevel.Standard;
    /// <summary>29.2 — the OP-Procedure KIND (masterdata.procedure_type). NULL on Lab and Radiology lines.
    /// Validated against the line's CPT section on the WRITE path by <c>ProcedureTypeRules.Validate</c>: an
    /// unvalidated type field is decorative, and every report built on it is quietly wrong.</summary>
    public string? ProcedureTypeCode { get; set; }

    /// <summary>
    /// 29.2 — what the doctor ASKED FOR. Set once at creation and never changed (design 45 §2).
    ///
    /// <para>Distinct from <see cref="QuantityOrdered"/>, which is what may actually be DELIVERED and is set
    /// from the APPROVED scope. Keeping both is what makes "how often are we approving less than we ask for?"
    /// answerable; overwriting the request on partial approval destroys the only signal that partial approval
    /// is happening at all.</para>
    /// </summary>
    public decimal RequestedQuantity { get; set; }

    /// <summary>
    /// 31.1 — how much of THIS item is delivered at each attendance (design 45 §2, revised).
    /// </summary>
    /// <remarks>
    /// Defaults to 1, under which a pre-31.1 row's stored total still equals its session count — which is
    /// what makes the migration an expand rather than a rewrite. Sessions and the KIND live on the ORDER now:
    /// a course is one clinical decision, and one number of attendances.
    /// </remarks>
    public decimal QuantityPerSession { get; set; } = 1m;

    /// <summary>The METERED TOTAL — what the atomic consume path decrements, what a partial approval narrows,
    /// and what the delivering centre counts down. For a session-based procedure it is
    /// <c>sessions x quantity-per-session</c> (<see cref="ProcedureCourse.MeteredTotal"/>); sessions delivered
    /// is derived from it rather than stored, never a parallel counter.</summary>
    public decimal QuantityOrdered { get; set; }
    public decimal QuantityConsumed { get; set; }        // accumulator, 0 ≤ consumed ≤ ordered (phase 5)
    public OrderLineStatus Status { get; set; } = OrderLineStatus.Active;
    public uint RowVersion { get; set; }                 // xmin — optimistic-concurrency guard on consume (phase 5)

    // ---- 30.1 the version chain (design 46 §1, orders 0013) ---------------------------------------------
    // A signed order is never edited. Amend INSERTS a new row and marks this one Superseded; the database
    // refuses an in-place edit of the clinical columns outright (trg_order_line_signed).

    /// <summary>1 for the original. Increments on each amendment.</summary>
    public int VersionNo { get; set; } = 1;
    /// <summary>The row this one replaces. NULL on v1.</summary>
    public Guid? SupersedesId { get; set; }
    /// <summary>The row that replaced this one. NON-NULL exactly when <see cref="Status"/> is
    /// <see cref="OrderLineStatus.Superseded"/> — enforced by a CHECK, so "superseded but pointing nowhere"
    /// is not a state that can exist.</summary>
    public Guid? SupersededById { get; set; }
    /// <summary>The FIRST version in this chain; itself on v1. Makes "every version of this line" one
    /// indexed query, which the service-history modal, the fulfiller's queue detail and order notes all
    /// need — a recursive walk would be re-derived, slightly differently, at each of the three.</summary>
    public Guid RootLineId { get; set; }

    public string? AmendmentReasonCode { get; set; }
    public string? AmendmentReasonText { get; set; }
    public Guid? AmendedBy { get; set; }
    public DateTimeOffset? AmendedAt { get; set; }

    public decimal QuantityRemaining => QuantityOrdered - QuantityConsumed;

    /// <summary>The line is finished and nothing further can be delivered against it. What
    /// <c>AmendableLine.IsTerminal</c> is fed from: a fully-consumed line is fact, and a cancelled or
    /// superseded one has already left the live set.</summary>
    public bool IsTerminal =>
        Status is OrderLineStatus.Completed or OrderLineStatus.Cancelled or OrderLineStatus.Superseded;
}

/// <summary>
/// 30.1 — one applied cancel or amend (design 46 §1/§7). APPEND-ONLY, enforced by a trigger, and keyed by a
/// UNIQUE <see cref="IdempotencyKey"/>: the same duplicate-proof anchor <see cref="OrderFulfillment"/> uses,
/// so a double-tapped cancel writes one record rather than two.
/// </summary>
public sealed class LineAmendmentRecord
{
    public Guid AmendmentId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid OrderId { get; set; }
    /// <summary>The line as it was when the action was taken — for an Amend, the row that became Superseded.</summary>
    public Guid OrderLineId { get; set; }
    /// <summary>The row an Amend created. NULL for a Cancel, which creates no successor.</summary>
    public Guid? NewLineId { get; set; }

    public string Action { get; set; } = default!;          // Cancel | Amend
    public string FromStatus { get; set; } = default!;
    public string ToStatus { get; set; } = default!;

    public string ReasonCode { get; set; } = default!;
    public string? ReasonText { get; set; }

    public Guid AmendedBy { get; set; }
    public string? AmendedByDisplay { get; set; }
    public DateTimeOffset AmendedAt { get; set; }

    public string IdempotencyKey { get; set; } = default!;  // UNIQUE — dedup guarantee
    public string? RequestHash { get; set; }
}

/// <summary>Append-only consume record (22-data-dictionary §7.3). One immutable row per consumed line: it is the
/// duplicate-proof anchor — <see cref="IdempotencyKey"/> is UNIQUE so a replayed key is rejected by the DB, and the
/// row can carry a result blob ref (phase 5.3). Never updated (except the one-time result attachment) or deleted;
/// full history lives in audit_event.</summary>
public sealed class OrderFulfillment
{
    public Guid FulfillmentId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid OrderLineId { get; set; }
    public Guid PerformingProviderId { get; set; }
    public decimal Quantity { get; set; }
    public string IdempotencyKey { get; set; } = default!;   // UNIQUE — dedup guarantee
    /// <summary>18.A3 — SHA-256 of the canonical request this row came from. A replay of the same key
    /// with a DIFFERENT payload is rejected instead of being answered with someone else's work. NULL on
    /// rows written before the column existed (treated as unverifiable, replay allowed).</summary>
    public string? RequestHash { get; set; }
    public Guid? ResultDocumentId { get; set; }              // phase 5.3 result blob ref
    public string? ResultValue { get; set; }                 // phase 5.3 structured result summary
    public DateTimeOffset? ResultUploadedAt { get; set; }
    public DateTimeOffset ConsumedAt { get; set; }
    public Guid ConsumedBy { get; set; }
}

/// <summary>Business-key formatter for orders (0A §3): <c>ORD-YYYY-NNNNNN</c>.</summary>
public static class OrderNo
{
    public static string Format(int year, int sequence) => $"ORD-{year:D4}-{sequence:D6}";
}

/// <summary>
/// 30.5b — a note on an order line (design 46 §7b, orders 0015). The doc-38 notes model on a different
/// subject: append-only (enforced by a trigger), signed with a SNAPSHOT of the author, cancellable but never
/// deletable, and class-projected so an external provider never receives an <c>Internal</c> note.
/// </summary>
public sealed class OrderNote
{
    public Guid NoteId { get; set; }
    public string TenantId { get; set; } = "";
    public string SubjectType { get; set; } = "OrderLine";
    public Guid SubjectId { get; set; }
    /// <summary>The line's CHAIN. A note written on v1 is about the clinical intent, which survives an
    /// amendment, so reads resolve by root and the note stays visible on v2.</summary>
    public Guid RootLineId { get; set; }
    public string Visibility { get; set; } = "ToFulfiller";
    public string Body { get; set; } = default!;
    public Guid AuthorUserId { get; set; }
    public string AuthorDisplayName { get; set; } = default!;
    public DateTimeOffset AuthoredAt { get; set; }
    public string Status { get; set; } = "Active";
    public Guid? CancelledBy { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
}
