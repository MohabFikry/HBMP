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
    public Guid PrescriptionId { get; set; }
    public Guid DrugId { get; set; }
    public string? Dose { get; set; }
    public string? Route { get; set; }
    public string? Frequency { get; set; }
    public decimal QuantityPrescribed { get; set; }
    public decimal QuantityDispensed { get; set; }       // accumulator, 0 ≤ dispensed ≤ prescribed (phase 6)
    public int RefillsAllowed { get; set; }
    public RxLineStatus Status { get; set; } = RxLineStatus.Active;
}

/// <summary>Referral lifecycle (§4): Requested → Accepted → Scheduled → Completed; plus Cancelled, Expired.
/// Phase 4.3 creates it in Requested; acceptance/scheduling/loop-closure are downstream.</summary>
public enum ReferralStatus { Requested, Accepted, Scheduled, Completed, Cancelled, Expired }

public sealed class Referral
{
    public Guid ReferralId { get; set; }
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
