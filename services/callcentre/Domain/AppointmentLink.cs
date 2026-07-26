namespace Mersal.CallCentre.Domain;

// Appointment actions from the call (phase 15.3). The Call Centre REUSES the emr appointment engine — it never
// writes appointments itself, so the no-double-book invariant, Idempotency-Key and If-Match all survive untouched.
// callcentre-service only records the LINK between an appointment change and the call that made it (so we never add
// a call column to emr's appointment table).

/// <summary>What the agent did to an appointment on the call.</summary>
public enum CallAppointmentAction { Book, Reschedule, Cancel }

/// <summary>Mandatory cancellation reason from the call centre (mirrors the DB CHECK). A cancel without one is 422.</summary>
public enum CallCancelReason
{
    PatientRequest,
    PatientUnwell,
    TransportIssue,
    Rescheduling,
    ClinicClosure,
    DuplicateBooking,
    Other,
}

/// <summary>Links an emr appointment change to the call_interaction that produced it (design 15.3 — "store the
/// interaction on the call-centre side"). Append-only, auditable.</summary>
public sealed class AppointmentLink
{
    public Guid LinkId { get; set; }
    public Guid InteractionId { get; set; }
    public string CallRef { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public Guid BeneficiaryId { get; set; }
    public Guid AppointmentId { get; set; }
    public CallAppointmentAction Action { get; set; }
    public CallCancelReason? CancelReason { get; set; }
    public string? BranchId { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
