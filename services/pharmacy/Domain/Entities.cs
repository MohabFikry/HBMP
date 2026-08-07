namespace Mersal.Pharmacy.Domain;

// Pharmacy domain (22-data-dictionary §8, 23-state-machines §3 Prescription / §4 Referral). Canonical enums.

/// <summary>Prescription lifecycle (§3): Draft → Submitted → (Approved|Rejected) → PartiallyDispensed →
/// Dispensed; plus Expired, Cancelled. Phase 4.3 covers create/submit and the auto-approve/route decision;
/// dispensing is phase 6.</summary>
public enum RxStatus { Draft, Submitted, Approved, Rejected, PartiallyDispensed, Dispensed, Expired, Cancelled }

public enum RxLineStatus { Active, PartiallyDispensed, Dispensed, Cancelled }

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
    public string? Dose { get; set; }
    public string? Route { get; set; }
    public string? Frequency { get; set; }
    public decimal QuantityPrescribed { get; set; }
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

    public decimal QuantityRemaining => QuantityPrescribed - QuantityDispensed;
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
