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
    /// <summary>When the clinician closed the visit; null while it is in progress (migration 0018).</summary>
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>Who closed it — display attribution, not the audit trail.</summary>
    public string? EndedBy { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>Encounter lifecycle rules (23 §6). Small enough to inline and important enough not to: "may this
/// visit be closed, and by whom" is asked by the endpoint and asserted by the tests, and the two must be
/// asking the same question.</summary>
public static class EncounterWorkflow
{
    /// <summary>Whether the encounter is in a state that can be closed at all.</summary>
    public static bool CanComplete(Encounter encounter) =>
        encounter is not null && encounter.Status == EncounterStatus.InProgress;

    /// <summary>Only the clinician who opened the visit closes it.
    ///
    /// <para>Not "any treating clinician": closing a visit stamps <c>ended_by</c> and moves the appointment
    /// to Completed, which is a statement that THIS consultation finished. A colleague who opens the record
    /// to read it must not be able to end someone else's consultation from under them — the same reasoning
    /// that lets only a note's author sign it.</para></summary>
    public static bool MayComplete(Encounter encounter, string? subject) =>
        encounter is not null
        && !string.IsNullOrEmpty(subject)
        && string.Equals(encounter.CreatedBy, subject, StringComparison.Ordinal);
}

/// <summary>A worklist entry so the checked-in beneficiary appears for the clinician.</summary>
/// <summary>
/// The encounter's open/closed bookkeeping — <b>not a queue</b>, despite the name.
///
/// <para>32.6 — one row is written per encounter and closed by EndVisit, so "Waiting" here means "this visit
/// is open", not "this person is in the waiting room". The platform's waiting room is
/// <see cref="QueueTicket"/>, issued at CHECK-IN and carrying branch scope, priority ordering and an audited
/// call-next. <c>GET /encounters/queue</c> presented this table as the second thing and was retired for it;
/// the name is left alone because renaming a persisted entity is a migration, and a comment that says what it
/// is costs nothing and reaches the next reader either way.</para>
/// </summary>
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
