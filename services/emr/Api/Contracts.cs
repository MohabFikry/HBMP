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
