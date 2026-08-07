namespace Mersal.Emr.Domain;

/// <summary>Appointment types (15-database-erd §7, 23-state-machines §6).</summary>
public enum AppointmentType { WalkIn, Scheduled, Referral, FollowUp }

/// <summary>Canonical PERSISTED appointment status (15-database-erd §7). New bookings start
/// <see cref="Booked"/>; <see cref="CheckedIn"/> is reached through the phase-2 visit gate; the
/// encounter close moves it to <see cref="Completed"/>. The pre-booking Requested/Waitlisted
/// sub-states of 23 §6 are modeled separately on <see cref="WaitlistEntry"/> — they are never stored
/// in <c>appointment.status</c>.</summary>
public enum AppointmentStatus { Booked, CheckedIn, Completed, NoShow, Cancelled }

/// <summary>Waitlist lifecycle (23 §6 Requested→Waitlisted→Scheduled/Expired). Promotion lands in 3.2.</summary>
public enum WaitlistStatus { Waitlisted, Promoted, Expired }

/// <summary>A booked appointment. A slot (<see cref="SlotId"/>) is held while the appointment is active
/// (Booked/CheckedIn); cancel/no-show release it (3.2). <c>beneficiary_id</c>/<c>provider_id</c>/
/// <c>location_id</c> are logical FKs to other services (no cross-schema constraint).</summary>
public sealed class Appointment
{
    public Guid AppointmentId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid BeneficiaryId { get; set; }
    public Guid ProviderId { get; set; }
    public Guid LocationId { get; set; }
    /// <summary>The Mersal branch this appointment belongs to (phase 14). NULL for a booking at an external
    /// provider location — branch scoping applies only to branch-bound rows (design 37 §3).</summary>
    public Guid? BranchId { get; set; }
    public Guid? SlotId { get; set; }
    /// <summary>The practitioner this appointment belongs to, when it belongs to one (phase 23). NULL for a
    /// general clinic session with no named doctor. Inherited from the slot when booked against one, or stated
    /// directly for a slotless walk-in — without it "the visits related to me" is not a query the doctor's
    /// worklist can ask.</summary>
    public Guid? DoctorId { get; set; }
    public AppointmentType AppointmentType { get; set; }
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Booked;
    public DateTimeOffset ScheduledStart { get; set; }
    public DateTimeOffset ScheduledEnd { get; set; }

    /// <summary>REF-* business key linked for a <see cref="AppointmentType.Referral"/> booking.</summary>
    public string? ReferralRef { get; set; }
    /// <summary>Originating encounter for a <see cref="AppointmentType.FollowUp"/> booking.</summary>
    public Guid? OriginEncounterId { get; set; }

    /// <summary>A short GENERAL/administrative note captured at booking — access needs, an interpreter, a
    /// preferred arrangement. Read by reception, the call centre and the treating doctor.
    /// <para><b>Never clinical.</b> No symptom, diagnosis, medication or result belongs here. The call centre
    /// writes this field and holds no clinical surface anywhere else on the platform, so a free-text box
    /// readable by a doctor is exactly where clinical detail would accumulate unless the boundary is stated
    /// and enforced. <see cref="AppointmentNote"/> caps and normalizes it; migration 0011 caps it again in the
    /// schema, because the API is not the only writer a table outlives.</para></summary>
    public string? Note { get; set; }

    /// <summary>Who last wrote <see cref="Note"/>, and when (0014). Display attribution, not an audit trail:
    /// the note crosses a team boundary — reception and the call centre write it, the treating doctor reads
    /// it — and an unattributed instruction is one nobody can follow up or date. audit-service holds the
    /// compliance record, but it needs <c>audit:read</c>, which none of the three readers has.</summary>
    public string? NoteBy { get; set; }
    public DateTimeOffset? NoteAt { get; set; }

    /// <summary>The author's display name, captured at the moment the note was written (0022).
    /// <para>A SNAPSHOT, never joined — 19.3's rule for signatures, and 0020's for allergen names. Without it
    /// the dialog rendered the raw subject id, which answers "who told us this?" with a string nobody at a
    /// desk can act on. <see cref="NoteBy"/> stays as the authoritative identity; this is only what the reader
    /// is shown. NULL for notes written before 0022, which readers say as "unknown" rather than inventing.
    /// </para></summary>
    public string? NoteByName { get; set; }

    /// <summary>The patient's display name, captured at BOOKING from the request (0013).
    /// <para>Minimum-necessary and a SNAPSHOT: a display name only, and deliberately not kept in sync with
    /// patient-service — it is what the appointment was booked under, and a name that silently changed
    /// underneath would make the desk's list disagree with the card the patient is holding. emr holds no
    /// demographics and never fetches this from a sibling; the operator already has it when they book.</para></summary>
    public string? BeneficiaryName { get; set; }

    /// <summary>Set when the assigned practitioner stopped serving this appointment's branch (14.5,
    /// <c>PractitionerBranchRevoked</c>). The appointment is NOT cancelled and NOT reassigned — both are
    /// decisions a background consumer must not make on a patient's behalf — it is marked so reception can
    /// ring them and rebook. Cleared implicitly when the appointment is rescheduled or cancelled.</summary>
    public DateTimeOffset? ReassignmentNeededAt { get; set; }

    public string? CancelReason { get; set; }
    /// <summary>Reporting flag (US-022): set when a Booked appointment is marked no-show. Populated in 3.2.</summary>
    public bool NoShow { get; set; }

    public string? IdempotencyKey { get; set; }
    public string? CreatedBy { get; set; }
    /// <summary>Who performed the most recent transition (phase 23). Snapshotted into appointment_history by
    /// the row trigger, which is what lets the visit timeline attribute each step. NULL on rows written before
    /// this existed — their transitions genuinely were not attributed.</summary>
    public string? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// 30.5c — when reception recorded the arrival (migration 0024, design 46 §7c).
    ///
    /// <para>NULL means <b>no check-in was recorded</b> — a walk-in taken straight into the room, or a missed
    /// step — and readers must say so rather than assuming the visit-start moment. Never derived from
    /// <see cref="UpdatedAt"/>, which every later transition overwrites: a waiting time computed from it is
    /// right on the day and quietly wrong by the end of the week.</para>
    /// </summary>
    public DateTimeOffset? CheckedInAt { get; set; }
    public string? CheckedInBy { get; set; }
    /// <summary>PostgreSQL <c>xmin</c> optimistic-concurrency token (drives the 3.2 If-Match ETag).</summary>
    public uint RowVersion { get; set; }
}

/// <summary>A recurring availability rule for a provider+location(+doctor): every <see cref="SlotMinutes"/>
/// between <see cref="StartTime"/> and <see cref="EndTime"/> on <see cref="DayOfWeek"/> is bookable.
/// Materialized into concrete <see cref="AppointmentSlot"/>s (23 §6 "recurring availability → slots").</summary>
public sealed class ProviderAvailability
{
    public Guid AvailabilityId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid ProviderId { get; set; }
    public Guid LocationId { get; set; }
    public Guid? BranchId { get; set; }   // phase 14 — Mersal branch (NULL = external provider location)
    public Guid? DoctorId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int SlotMinutes { get; set; }
}

/// <summary>A concrete bookable slot. Holds at most one active appointment — enforced by a partial UNIQUE
/// index on <c>appointment.slot_id</c> WHERE status IN ('Booked','CheckedIn') (3.1 no-double-book).</summary>
public sealed class AppointmentSlot
{
    public Guid SlotId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid ProviderId { get; set; }
    public Guid LocationId { get; set; }
    public Guid? BranchId { get; set; }   // phase 14 — Mersal branch (NULL = external provider location)
    public Guid? DoctorId { get; set; }
    public DateTimeOffset SlotStart { get; set; }
    public DateTimeOffset SlotEnd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A waitlist entry created when no slot is available and the caller opts in (US-020). Promotion on a
/// freed slot arrives in 3.2.</summary>
public sealed class WaitlistEntry
{
    public Guid WaitlistId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid BeneficiaryId { get; set; }
    public Guid ProviderId { get; set; }
    public Guid LocationId { get; set; }
    public Guid? BranchId { get; set; }   // phase 14 — Mersal branch (NULL = external provider location)
    public AppointmentType AppointmentType { get; set; }
    public int PriorityScore { get; set; }
    public WaitlistStatus Status { get; set; } = WaitlistStatus.Waitlisted;
    public string? ReferralRef { get; set; }
    public Guid? OriginEncounterId { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>At-least-once ledger for this service's event consumers: a redelivered event id short-circuits,
/// so a broker retry cannot re-apply work a human has already dealt with. Same shape as policy-service's.</summary>
public sealed class ProcessedEvent
{
    public Guid EventId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
