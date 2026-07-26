namespace Mersal.Emr.Domain;

/// <summary>Encounter lifecycle (23-state-machines §6). Phase 2.3 is a STUB — SOAP/diagnoses/orders
/// arrive in phase 4; here we create only the encounter shell + a clinician queue entry.</summary>
public enum EncounterStatus { InProgress, Completed, Cancelled }

public enum QueueState { Waiting, InConsultation, Done }

public sealed class Encounter
{
    public Guid EncounterId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public string EncounterNo { get; set; } = default!;   // ENC-YYYY-NNNNNN
    public Guid BeneficiaryId { get; set; }
    public Guid? AppointmentId { get; set; }
    public Guid? ProviderId { get; set; }
    public EncounterStatus Status { get; set; } = EncounterStatus.InProgress;
    public DateTimeOffset StartedAt { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>A worklist entry so the checked-in beneficiary appears for the clinician.</summary>
public sealed class QueueEntry
{
    public Guid QueueEntryId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid EncounterId { get; set; }
    public Guid BeneficiaryId { get; set; }
    public Guid? ProviderId { get; set; }
    public QueueState State { get; set; } = QueueState.Waiting;
    public DateTimeOffset EnqueuedAt { get; set; }
}

/// <summary>Business-key formatter for encounters (0A §3): <c>ENC-YYYY-NNNNNN</c>.</summary>
public static class EncounterNo
{
    public static string Format(int year, int sequence) => $"ENC-{year:D4}-{sequence:D6}";
}
