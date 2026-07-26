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
    public AppointmentType AppointmentType { get; set; }
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Booked;
    public DateTimeOffset ScheduledStart { get; set; }
    public DateTimeOffset ScheduledEnd { get; set; }

    /// <summary>REF-* business key linked for a <see cref="AppointmentType.Referral"/> booking.</summary>
    public string? ReferralRef { get; set; }
    /// <summary>Originating encounter for a <see cref="AppointmentType.FollowUp"/> booking.</summary>
    public Guid? OriginEncounterId { get; set; }

    public string? CancelReason { get; set; }
    /// <summary>Reporting flag (US-022): set when a Booked appointment is marked no-show. Populated in 3.2.</summary>
    public bool NoShow { get; set; }

    public string? IdempotencyKey { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
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
