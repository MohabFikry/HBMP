using Mersal.Emr.Domain;

namespace Mersal.Emr.Api;

/// <summary>POST /encounters — start a visit for a beneficiary (17-api-specifications §6).</summary>
public sealed record CreateEncounterRequest(Guid BeneficiaryId, Guid? AppointmentId, Guid? ProviderId);

public sealed record EncounterResponse(
    Guid EncounterId, string EncounterNo, Guid BeneficiaryId, Guid? AppointmentId,
    Guid? ProviderId, string Status, DateTimeOffset StartedAt)
{
    public static EncounterResponse From(Encounter e) => new(
        e.EncounterId, e.EncounterNo, e.BeneficiaryId, e.AppointmentId, e.ProviderId, e.Status.ToString(), e.StartedAt);
}

public sealed record QueueItemResponse(
    Guid QueueEntryId, Guid EncounterId, Guid BeneficiaryId, Guid? ProviderId, string State, DateTimeOffset EnqueuedAt)
{
    public static QueueItemResponse From(QueueEntry q) => new(
        q.QueueEntryId, q.EncounterId, q.BeneficiaryId, q.ProviderId, q.State.ToString(), q.EnqueuedAt);
}

// ---- Phase 3.1 appointments (17-api-specifications §6, US-020) ----

/// <summary>Materialize bookable slots from a recurring availability rule over an inclusive date range.</summary>
/// <summary>18.C2 (W7): <c>BranchId</c> is optional and, when present alongside <c>DoctorId</c>, is validated
/// against the practitioner's active branch assignments (FR-BRN-026) before any slot is materialized.</summary>
public sealed record CreateSlotsRequest(
    Guid ProviderId, Guid LocationId, Guid? DoctorId,
    DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime, int SlotMinutes,
    DateOnly FromDate, DateOnly ToDate, Guid? BranchId = null);

/// <summary>Book an appointment. Provide <see cref="SlotId"/> to hold a specific slot; omit it (non-walk-in)
/// to auto-take the earliest open slot. Walk-ins may be slotless (uses <see cref="ScheduledStart"/>).</summary>
public sealed record BookAppointmentRequest(
    Guid BeneficiaryId, Guid ProviderId, Guid LocationId, string AppointmentType,
    Guid? SlotId, DateTimeOffset? ScheduledStart, DateTimeOffset? ScheduledEnd,
    string? ReferralRef, Guid? OriginEncounterId, bool JoinWaitlistIfFull,
    string? PreferredChannel = null,
    // 18.C2 (W7 / FR-BRN-027): validated against the practitioner's branch assignments. Optional because a
    // walk-in at the desk names neither — the check applies when the caller states both.
    Guid? DoctorId = null, Guid? BranchId = null);

/// <summary>Minimum-necessary appointment view — scheduling + identity only, never EMR/clinical data.
/// <see cref="RowVersion"/> is the row's <c>xmin</c> optimistic-concurrency token: it lets a client echo the
/// value it read as <c>If-Match</c> on a transition (opt-in 412 on a stale write). It is surfaced on every
/// row so the LIST endpoint carries a per-row token too, where a per-response <c>ETag</c> header cannot.</summary>
public sealed record AppointmentResponse(
    Guid AppointmentId, Guid BeneficiaryId, Guid ProviderId, Guid LocationId, Guid? SlotId,
    string AppointmentType, string Status, DateTimeOffset ScheduledStart, DateTimeOffset ScheduledEnd,
    string? ReferralRef, Guid? OriginEncounterId, uint RowVersion, Guid? BranchId)
{
    public static AppointmentResponse From(Appointment a) => new(
        a.AppointmentId, a.BeneficiaryId, a.ProviderId, a.LocationId, a.SlotId,
        a.AppointmentType.ToString(), a.Status.ToString(), a.ScheduledStart, a.ScheduledEnd,
        a.ReferralRef, a.OriginEncounterId, a.RowVersion, a.BranchId);
}

public sealed record SlotResponse(
    Guid SlotId, Guid ProviderId, Guid LocationId, Guid? DoctorId,
    DateTimeOffset SlotStart, DateTimeOffset SlotEnd, bool Open)
{
    public static SlotResponse From(AppointmentSlot s, bool open) => new(
        s.SlotId, s.ProviderId, s.LocationId, s.DoctorId, s.SlotStart, s.SlotEnd, open);
}

public sealed record WaitlistResponse(Guid WaitlistId, string Status, int PriorityScore)
{
    public static WaitlistResponse From(WaitlistEntry w) => new(w.WaitlistId, w.Status.ToString(), w.PriorityScore);
}

// ---- Phase 3.2 transitions ----

/// <summary>Reschedule an appointment onto a different slot (releases the old one atomically).</summary>
public sealed record RescheduleRequest(Guid NewSlotId);

public sealed record CancelRequest(string? Reason);

// ---- Phase 3.3 queue + reminders ----

/// <summary>Check in an arrived beneficiary. <see cref="MemberNo"/>/<see cref="DisplayName"/> are the
/// minimum-necessary display identity for the queue (reception already sees these); no clinical data.</summary>
public sealed record CheckInRequest(string? MemberNo, string? DisplayName, int Priority);

/// <summary>Minimum-necessary queue row — position, display identity, type, wait time. NEVER EMR/clinical
/// fields (enforced by <c>QueueMinNecessaryTests</c>).</summary>
public sealed record QueueItemView(
    Guid QueueId, Guid AppointmentId, int Position, string? MemberNo, string? DisplayName,
    string AppointmentType, string State, long WaitSeconds)
{
    public static QueueItemView From(QueueTicket t, int position, DateTimeOffset now) => new(
        t.QueueId, t.AppointmentId, position, t.MemberNo, t.DisplayName,
        t.AppointmentType.ToString(), t.State.ToString(), (long)(now - t.EnqueuedAt).TotalSeconds);
}

/// <summary>A clinic the caller may book into, derived from the SLOTS that exist rather than from the provider
/// directory (which reception is correctly refused). Ids plus a count — names are a separate label lookup, so
/// this endpoint cannot become a way to enumerate the network.</summary>
public sealed record BranchClinicResponse(Guid ProviderId, Guid LocationId, Guid? BranchId, int OpenSlots);
