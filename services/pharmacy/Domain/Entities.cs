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
    public Guid? AuthorizationId { get; set; }
    public RxStatus Status { get; set; } = RxStatus.Draft;
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? CreatedBy { get; set; }
    public uint RowVersion { get; set; }
    public List<PrescriptionLine> Lines { get; set; } = [];
}

public sealed class PrescriptionLine
{
    public Guid PrescriptionLineId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid PrescriptionId { get; set; }
    public Guid DrugId { get; set; }
    public string? Dose { get; set; }
    public string? Route { get; set; }
    public string? Frequency { get; set; }
    public decimal QuantityPrescribed { get; set; }
    public decimal QuantityDispensed { get; set; }       // accumulator, 0 ≤ dispensed ≤ prescribed (phase 6)
    public int RefillsAllowed { get; set; }
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
