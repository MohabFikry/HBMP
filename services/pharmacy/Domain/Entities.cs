namespace Mersal.Pharmacy.Domain;

// Pharmacy domain (22-data-dictionary §8, 23-state-machines §3 Prescription / §4 Referral). Canonical enums.

/// <summary>Prescription lifecycle (§3): Draft → Submitted → (Approved|Rejected) → PartiallyDispensed →
/// Dispensed; plus Expired, Cancelled. Phase 4.3 covers create/submit and the auto-approve/route decision;
/// dispensing is phase 6.</summary>
public enum RxStatus { Draft, Submitted, Approved, Rejected, PartiallyDispensed, Dispensed, Expired, Cancelled }

/// <summary>30.1 — <see cref="Superseded"/> is the state a line enters when it is AMENDED: the row is never
/// mutated, a new version is inserted, and this one steps aside pointing at its successor (design 46 §1).
/// It is a line status only; there is deliberately no prescription status of the same name — see pharmacy 0013.</summary>
public enum RxLineStatus { Active, PartiallyDispensed, Dispensed, Cancelled, Superseded }

public sealed class Prescription
{
    public Guid PrescriptionId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public string RxNo { get; set; } = default!;         // RX-YYYY-NNNNNN
    public Guid BeneficiaryId { get; set; }
    public Guid EncounterId { get; set; }
    public Guid PrescriberId { get; set; }
    /// <summary>The prescriber's display name, snapshot at submission (migration 0006). NULL for rows
    /// written before it — readers say "(not recorded)" rather than showing the uuid.</summary>
    public string? PrescriberName { get; set; }
    public Guid? AuthorizationId { get; set; }
    public RxStatus Status { get; set; } = RxStatus.Draft;
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>The approvals authorization that put this prescription back in date, if any. Doubles as the
    /// idempotency key for the apply — a retried callback for the same authorization grants no second period.</summary>
    public Guid? ValidityExtendedBy { get; set; }
    public DateTimeOffset? ValidityExtendedAt { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? CreatedBy { get; set; }

    /// <summary>26.4 — the encounter's primary diagnosis at prescribing time, for quick filtering.</summary>
    public string? PrimaryIcdCode { get; set; }

    /// <summary>
    /// 26.4 — the encounter's recorded ICD codes AS AT prescribing time, as a JSON array.
    /// </summary>
    /// <remarks>
    /// A snapshot, not a join. The indication check is a statement about what was known when the
    /// prescription was written; if a diagnosis is corrected next week, the record of what was actually
    /// checked must not change to match.
    /// </remarks>
    public string? DiagnosisSnapshot { get; set; }

    // ---- 29.5 — acute / chronic (design 45 §5) ----------------------------------------------------------

    /// <summary>"Acute" (today's behaviour, unchanged) or "Chronic".</summary>
    public string Kind { get; set; } = "Acute";

    /// <summary>The supervisor-configurable refill cadence (<c>pharmacy.refill_frequency</c>). NULL on an
    /// acute script, and the CHECK enforces that — so "is this chronic?" has exactly one answer.</summary>
    public string? RefillFrequencyCode { get; set; }

    /// <summary>Treatment length. Chronic requires &gt; 30: "a 14-day course is not chronic".</summary>
    public int? DurationDays { get; set; }

    /// <summary>The script's validity spans the WHOLE duration; it is dispensable in windows within it.</summary>
    public DateOnly? ValidFrom { get; set; }
    public DateOnly? ValidUntil { get; set; }

    public uint RowVersion { get; set; }
    public List<PrescriptionLine> Lines { get; set; } = [];
}

/// <summary>
/// 29.5 — one refill window of a chronic script (design 45 §5). PER LINE, because lines can have different
/// durations.
///
/// <para><b><see cref="Status"/> is stored for Blocked and Missed, and only for those.</b> Both are EVENTS
/// with money consequences that need a timestamp; <c>Open</c> is never written, because dispensability is
/// computed from the dates. A stalled sweeper must delay a forfeiture, never refuse a patient at the
/// counter — see docs/superpowers/specs/2026-08-07-chronic-refill-windows-design.md.</para>
/// </summary>
public sealed class PrescriptionDispenseWindow
{
    public Guid WindowId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid PrescriptionId { get; set; }
    public Guid PrescriptionLineId { get; set; }
    public int WindowNo { get; set; }

    /// <summary>What the patient is told. The early tolerance never moves this — moving it would pull the
    /// whole rest of the schedule forward, which is exactly what a FIXED window exists to prevent.</summary>
    public DateOnly ScheduledOpenDate { get; set; }

    /// <summary>Scheduled minus the early tolerance. STORED rather than computed: the tolerance is
    /// configurable, and a window issued under a 5-day tolerance keeps it if the setting later changes.</summary>
    public DateOnly OpensAt { get; set; }
    public DateOnly ClosesAt { get; set; }

    public decimal AllocatedQuantity { get; set; }
    public decimal DispensedQuantity { get; set; }

    public string Status { get; set; } = "Pending";

    /// <summary>Why eligibility refused. A block with no reason is not "visible to the case team" — it is a
    /// stuck row nobody can explain.</summary>
    public string? BlockedReason { get; set; }

    /// <summary>When the forfeiture was recorded. Without it, "had the member's coverage already lapsed by
    /// then?" is unanswerable.</summary>
    public DateTimeOffset? MissedAt { get; set; }

    /// <summary>30.3 — the amendment that replaced this window (pharmacy 0014). Set only on <c>Superseded</c>
    /// rows: a window the prescriber's duration or frequency change made obsolete. NOT <c>Missed</c> (the
    /// patient did not fail to attend) and NOT <c>Cancelled</c> (nobody withdrew the medicine).</summary>
    public Guid? SupersededByAmendmentId { get; set; }

    /// <summary>xmin — the sweeper and the counter both write this row, and exactly one must win.</summary>
    public uint RowVersion { get; set; }
}

public sealed class PrescriptionLine
{
    public Guid PrescriptionLineId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid PrescriptionId { get; set; }
    public Guid DrugId { get; set; }
    /// <summary>The product's name as master data gave it when this was prescribed (migration 0006) —
    /// trade name, strength and form, because that is what identifies the box on the shelf. NULL for rows
    /// written before it; a dispensing screen shows "(not recorded)", never the uuid.</summary>
    public string? DrugName { get; set; }
    /// <summary>The sig a pharmacist reads — "1 Tablet x 3/day". DERIVED from the two numbers below.</summary>
    public string? Dose { get; set; }

    /// <summary>
    /// 31.5 — how much per administration, in the drug's prescribing unit.
    /// </summary>
    /// <remarks>
    /// <para>The number the daily-dose rule was compared against and the quantity check divided by. It used
    /// to arrive on every line, drive both, and then be discarded — leaving <see cref="Dose"/>, a sentence
    /// this application formatted, as the only trace. A prescription could not be re-checked against the
    /// numbers it was written from without parsing that sentence back, which is reading clinical values out
    /// of display text.</para>
    ///
    /// <para>NULL on a line written before 31.5. Never 1: a default here would assert a dose nobody
    /// wrote.</para>
    /// </remarks>
    public decimal? DoseAmount { get; set; }

    /// <summary>31.5 — administrations per day. See <see cref="DoseAmount"/>.</summary>
    public int? TimesPerDay { get; set; }

    public string? Route { get; set; }
    public string? Frequency { get; set; }
    public decimal QuantityPrescribed { get; set; }

    /// <summary>
    /// 31.3 — what <see cref="QuantityPrescribed"/> COUNTS: "boxes", or the prescribing unit ("tabs", "IU").
    /// </summary>
    /// <remarks>
    /// A quantity of 1 against a 24-tablet box and a quantity of 2250 against a box of insulin pens are both
    /// correct and are counted in different things, and a dispensing screen shows the figure alone. A
    /// SNAPSHOT taken at prescribing time for the same reason <see cref="DrugName"/> is one: what the
    /// catalogue says today must not change what a prescription written last year meant. NULL where the
    /// catalogue records no unit — rendered as no unit, never as a guess.
    /// </remarks>
    public string? QuantityUnit { get; set; }

    public decimal QuantityDispensed { get; set; }       // accumulator, 0 ≤ dispensed ≤ prescribed (phase 6)
    public int RefillsAllowed { get; set; }

    /// <summary>
    /// 26.4 — treatment length in days. New in phase 26: the line carried dose, route, frequency and
    /// quantity but no duration, and duration is what makes a daily-dose ceiling or a treatment-length
    /// limit checkable at all.
    /// </summary>
    public int? DurationDays { get; set; }

    public RxLineStatus Status { get; set; } = RxLineStatus.Active;
    public uint RowVersion { get; set; }                 // xmin — optimistic-concurrency guard on dispense (phase 6)

    // ---- 30.1 the version chain (design 46 §1, pharmacy 0013) -------------------------------------------
    // A signed prescription is never edited. Amend INSERTS a new row and marks this one Superseded; the
    // database refuses an in-place edit of drug, dose, route, frequency, quantity, duration or refills
    // outright (trg_rx_line_signed).

    public int VersionNo { get; set; } = 1;
    public Guid? SupersedesId { get; set; }
    /// <summary>NON-NULL exactly when <see cref="Status"/> is <see cref="RxLineStatus.Superseded"/>, by CHECK.</summary>
    public Guid? SupersededById { get; set; }
    /// <summary>The first version in this chain; itself on v1. A chronic line's refill windows follow it.</summary>
    public Guid RootLineId { get; set; }

    public string? AmendmentReasonCode { get; set; }
    public string? AmendmentReasonText { get; set; }
    public Guid? AmendedBy { get; set; }
    public DateTimeOffset? AmendedAt { get; set; }

    // ---- 6.3 the shortage the counter reports (design 49 §5, pharmacy 0020) ------------------------------
    //
    // A fact about the PHARMACY, not about the prescription. None of these touch the accumulator or
    // `Status`: the line stays dispensable and `QuantityRemaining` is unchanged, because stock arriving
    // tomorrow must not require anything to be undone.
    //
    // The endpoint that writes them has existed since phase 6.3 — publishing to the prescriber, escalating
    // to the pharmacy supervisor after eight hours, audited — and stored nothing, so before 0020 the flag
    // the contract promised the screen could not survive a page reload and re-raising it notified again
    // every time.

    /// <summary>When the counter reported it could not fill this line, or NULL if it never has.</summary>
    /// <remarks>NULL is "never reported", NOT "in stock". Whether a product is on a shelf is
    /// inventory-service's fact; this table only knows what a counter said on a day.</remarks>
    public DateTimeOffset? OutOfStockAt { get; set; }

    /// <summary>The pharmacist who reported it. Present exactly when <see cref="OutOfStockAt"/> is, by CHECK.</summary>
    public string? OutOfStockBy { get; set; }

    /// <summary>How much could not be filled, in this line's <see cref="QuantityUnit"/>. NULL means the whole
    /// remaining quantity — stored as absent rather than as a copy of <see cref="QuantityRemaining"/>, which
    /// would go stale the moment a partial dispense landed.</summary>
    public decimal? OutOfStockQty { get; set; }

    /// <summary>The pharmacist's note to the prescriber. Never carried in the notification body — an inbox
    /// line is read by whoever holds the device.</summary>
    public string? OutOfStockNote { get; set; }

    /// <summary>Reported short and not yet filled since.</summary>
    public bool OutOfStock => OutOfStockAt is not null;

    public decimal QuantityRemaining => QuantityPrescribed - QuantityDispensed;

    /// <summary>The line is finished and nothing further can be dispensed against it.</summary>
    public bool IsTerminal =>
        Status is RxLineStatus.Dispensed or RxLineStatus.Cancelled or RxLineStatus.Superseded;
}

/// <summary>
/// 30.1 — one applied cancel or amend on a prescription line (design 46 §1/§7). APPEND-ONLY, enforced by a
/// trigger, keyed by a UNIQUE <see cref="IdempotencyKey"/> — the same duplicate-proof anchor
/// <see cref="DispenseEvent"/> uses, so a double-tapped cancel writes one record rather than two.
/// </summary>
public sealed class LineAmendmentRecord
{
    public Guid AmendmentId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid PrescriptionId { get; set; }
    public Guid PrescriptionLineId { get; set; }
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

/// <summary>Append-only dispense record (22-data-dictionary §8.3, extended with <see cref="ExpiryDate"/> for lot
/// expiry). One immutable row per dispense: it is the duplicate-proof anchor — <see cref="IdempotencyKey"/> is UNIQUE
/// so a replayed key is rejected by the DB and mapped to "return prior outcome". Batch + expiry are captured on every
/// dispense; a policy-approved substitution records <see cref="SubstitutedDrugId"/> + <see cref="SubstitutionReason"/>.
/// Never updated or soft-deleted — full history lives in audit_event.</summary>
public sealed class DispenseEvent
{
    public Guid DispenseId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid PrescriptionLineId { get; set; }
    public Guid DispensingPharmacyId { get; set; }
    public decimal Quantity { get; set; }
    public string IdempotencyKey { get; set; } = default!;   // UNIQUE — dedup guarantee
    /// <summary>18.A3 — SHA-256 of the canonical dispense request this row came from, so a replay with a
    /// changed quantity/batch/substitution is rejected rather than silently answered with the original.
    /// NULL on rows written before the column existed (unverifiable, replay allowed).</summary>
    public string? RequestHash { get; set; }
    public string BatchNo { get; set; } = default!;
    public DateOnly ExpiryDate { get; set; }
    public Guid? SubstitutedDrugId { get; set; }             // phase 6.3 — policy-approved alternative actually dispensed
    public string? SubstitutionReason { get; set; }
    /// <summary>
    /// What the pharmacist recorded about THIS handover — collection arrangements, a replaced lot, who
    /// collected on the patient's behalf.
    /// </summary>
    /// <remarks>
    /// Not a clinical note and never read by the clinical checks. It rides on the dispense because it
    /// describes that act at that counter, not the prescriber's decision — and because a pharmacist who needs
    /// to tell a PRESCRIBER something has the out-of-stock notice, the substitution reason and the approval
    /// team. Capped at 500 characters by the database (migration 0011): this table is append-only, so a field
    /// with no ceiling is one somebody eventually pastes a clinical history into, permanently.
    /// </remarks>
    public string? Note { get; set; }
    public DateTimeOffset DispensedAt { get; set; }
    public Guid DispensedBy { get; set; }
}

/// <summary>Referral lifecycle (§4): Requested → Accepted → Scheduled → Completed; plus Cancelled, Expired.
/// Phase 4.3 creates it in Requested; acceptance/scheduling/loop-closure are downstream.</summary>
public enum ReferralStatus { Requested, Accepted, Scheduled, Completed, Cancelled, Expired }

public sealed class Referral
{
    public Guid ReferralId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public string ReferralNo { get; set; } = default!;   // REF-YYYY-NNNNNN
    public Guid BeneficiaryId { get; set; }
    public Guid EncounterId { get; set; }
    public Guid ReferringProviderId { get; set; }
    public string TargetSpecialty { get; set; } = default!;
    public Guid? TargetProviderId { get; set; }
    public string? Reason { get; set; }

    /// <summary>
    /// 29.2 — the CPT code this referral was raised FOR (design 45 §2). NULL means NOT RECORDED, which is
    /// not the same as "no service": referrals raised before this existed, and those raised from paths that
    /// carry no code, are legitimately null.
    /// </summary>
    public string? RequestedServiceCode { get; set; }

    /// <summary>The coding system of <see cref="RequestedServiceCode"/>. Named, never assumed.</summary>
    public string? RequestedServiceCodeSystem { get; set; }

    public ReferralStatus Status { get; set; } = ReferralStatus.Requested;
    public DateTimeOffset RequestedAt { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? CreatedBy { get; set; }
}

public static class RxNo
{
    public static string Format(int year, int sequence) => $"RX-{year:D4}-{sequence:D6}";
}

public static class ReferralNo
{
    public static string Format(int year, int sequence) => $"REF-{year:D4}-{sequence:D6}";
}

/// <summary>
/// 26.4 — an append-only record of one validation run (doc 43 §5).
/// </summary>
/// <remarks>
/// Never updated and never deleted. It is the evidence of what the prescriber was shown at step 1 and what
/// the server concluded at step 2 — and since the two evaluate independently, a divergence between them is
/// normal and must be inspectable rather than resolved silently.
/// </remarks>
public sealed class PrescriptionValidationRun
{
    public Guid ValidationId { get; set; }
    public string TenantId { get; set; } = "";

    /// <summary>Null for a draft run: the doctor validates while composing, before anything is submitted.</summary>
    public Guid? PrescriptionId { get; set; }

    public Guid EncounterId { get; set; }
    public Guid BeneficiaryId { get; set; }
    public DateTimeOffset RanAt { get; set; }
    public string? RanBy { get; set; }

    /// <summary>"Step1" (advisory, client-facing) or "Step2" (authoritative, server-side on submit).</summary>
    public string Step { get; set; } = "Step1";

    public string EngineVersion { get; set; } = default!;
    public string OverallState { get; set; } = default!;

    /// <summary>The findings, serialized. Stored whole so a later reviewer sees exactly what was produced.</summary>
    public string Findings { get; set; } = "[]";
}

/// <summary>
/// 26.4 — a clinician's recorded reason for proceeding past a warning (doc 43 §1 rule 3).
/// </summary>
/// <remarks>
/// Overrides are expected and recorded, not prevented: blocking a prescriber on automated advice of
/// uncertain provenance would be the greater harm. The reason is mandatory because an acknowledgement with
/// no reason is a click, and a click is not a justification — it is also what the approver later reads.
/// </remarks>
public sealed class PrescriptionLineOverride
{
    public Guid OverrideId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid PrescriptionId { get; set; }
    public Guid LineId { get; set; }
    public string FindingKind { get; set; } = default!;
    public string? FindingRef { get; set; }
    public string Reason { get; set; } = default!;
    public string AcknowledgedBy { get; set; } = default!;
    public DateTimeOffset AcknowledgedAt { get; set; }
}

/// <summary>
/// 29.5 — a supervisor-configurable refill cadence (<c>pharmacy.refill_frequency</c>, migration 0012).
///
/// <para>Data rather than an enum, for the reason the migration records: adding "every 4 months" must be an
/// INSERT, not a release. <see cref="Months"/> is what the window arithmetic multiplies by.</para>
/// </summary>
public sealed class RefillFrequency
{
    public string Code { get; set; } = default!;
    public int Months { get; set; }
    public string NameEn { get; set; } = default!;
    public string NameAr { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>
/// 32.5 — an operational note on a prescription line (design 46 §7b).
/// </summary>
/// <remarks>
/// The <c>orders.OrderNote</c> model on a different subject, column for column. Doc 46 §7b requires the
/// reuse and says why: a second notes mechanism means two behaviours for "cancel a note" and two answers to
/// "who can read this".
///
/// <para><see cref="RootLineId"/> rather than the line id is the anchor, because 30.1 supersedes a line
/// instead of mutating it: a note is written about the clinical INTENT, and the intent survives an
/// amendment. Keying on the line would silently drop every instruction attached to a script the moment it
/// was amended.</para>
/// </remarks>
public sealed class PrescriptionNote
{
    public Guid NoteId { get; set; }
    public string TenantId { get; set; } = "";
    public string SubjectType { get; set; } = "PrescriptionLine";
    public Guid SubjectId { get; set; }
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
