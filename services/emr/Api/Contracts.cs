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
public sealed record CreateSlotsRequest(
    Guid ProviderId, Guid LocationId, Guid? DoctorId,
    DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime, int SlotMinutes,
    DateOnly FromDate, DateOnly ToDate);

/// <summary>Book an appointment. Provide <see cref="SlotId"/> to hold a specific slot; omit it (non-walk-in)
/// to auto-take the earliest open slot. Walk-ins may be slotless (uses <see cref="ScheduledStart"/>).</summary>
public sealed record BookAppointmentRequest(
    Guid BeneficiaryId, Guid ProviderId, Guid LocationId, string AppointmentType,
    Guid? SlotId, DateTimeOffset? ScheduledStart, DateTimeOffset? ScheduledEnd,
    string? ReferralRef, Guid? OriginEncounterId, bool JoinWaitlistIfFull);

/// <summary>Minimum-necessary appointment view — scheduling + identity only, never EMR/clinical data.</summary>
public sealed record AppointmentResponse(
    Guid AppointmentId, Guid BeneficiaryId, Guid ProviderId, Guid LocationId, Guid? SlotId,
    string AppointmentType, string Status, DateTimeOffset ScheduledStart, DateTimeOffset ScheduledEnd,
    string? ReferralRef, Guid? OriginEncounterId)
{
    public static AppointmentResponse From(Appointment a) => new(
        a.AppointmentId, a.BeneficiaryId, a.ProviderId, a.LocationId, a.SlotId,
        a.AppointmentType.ToString(), a.Status.ToString(), a.ScheduledStart, a.ScheduledEnd,
        a.ReferralRef, a.OriginEncounterId);
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
