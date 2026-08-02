using Mersal.Emr.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Infrastructure;

/// <summary>
/// Appends steps to a care episode (ADR-0031).
///
/// <para><b>It does not save.</b> Every caller is already inside a transaction that is doing the thing the
/// step describes, and a step that commits separately from its cause is a timeline that can claim a visit
/// ended when the visit did not. So this stages the row and leaves the commit to whoever owns it.</para>
/// </summary>
public sealed class CareTimelineWriter(EmrDbContext db, TimeProvider clock)
{
    /// <summary>Stage one step against an encounter. `appointmentId` is carried when the episode has a
    /// parent — the timeline is read from both ends.</summary>
    public void Add(
        string step, Guid beneficiaryId,
        Guid? encounterId = null, Guid? appointmentId = null,
        string? actor = null, string? reference = null,
        string source = CareStepSources.Emr, Guid? eventId = null, DateTimeOffset? occurredAt = null)
    {
        db.CareTimeline.Add(new CareStep
        {
            StepId = Guid.NewGuid(),
            // TenantId is left to TenantStampingInterceptor, exactly as it is for every other entity created
            // in endpoint code. Setting it here from an entity that has not been saved yet would copy the
            // empty default and defeat the interceptor's own "fail rather than write an unscoped row" guard.
            EncounterId = encounterId,
            AppointmentId = appointmentId,
            BeneficiaryId = beneficiaryId,
            Step = step,
            OccurredAt = occurredAt ?? clock.GetUtcNow(),
            Actor = actor,
            Source = source,
            Reference = reference,
            EventId = eventId,
        });
    }

    /// <summary>The episode for one appointment: its own steps plus those of the encounter it produced.
    ///
    /// <para>Oldest first, because a timeline read whole is read forwards. The appointment's STATUS history
    /// is a separate source and is merged by the endpoint — this returns only what the episode recorded.</para>
    /// </summary>
    public async Task<List<CareStep>> ForAppointmentAsync(Guid appointmentId, CancellationToken ct = default)
    {
        var encounterIds = await db.Encounters.AsNoTracking()
            .Where(e => e.AppointmentId == appointmentId)
            .Select(e => e.EncounterId)
            .ToListAsync(ct);

        return await db.CareTimeline.AsNoTracking()
            .Where(s => s.AppointmentId == appointmentId
                        || (s.EncounterId != null && encounterIds.Contains(s.EncounterId.Value)))
            .OrderBy(s => s.OccurredAt)
            .ToListAsync(ct);
    }

    /// <summary>The episode for one visit — what the encounter workspace shows.</summary>
    public Task<List<CareStep>> ForEncounterAsync(Guid encounterId, CancellationToken ct = default) =>
        db.CareTimeline.AsNoTracking()
            .Where(s => s.EncounterId == encounterId)
            .OrderBy(s => s.OccurredAt)
            .ToListAsync(ct);
}
