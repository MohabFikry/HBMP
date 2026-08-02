using Mersal.Emr.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mersal.Emr.Infrastructure;

/// <summary>What became of a step a sibling service asked for.</summary>
public enum CareStepOutcome
{
    /// <summary>Written.</summary>
    Appended,
    /// <summary>Already written — this event has been delivered before. Not a failure.</summary>
    Duplicate,
    /// <summary>No such encounter in this tenant. Not a failure either; see the remarks on
    /// <see cref="CareEpisodeAppender"/>.</summary>
    UnknownEncounter,
}

/// <summary>
/// Appends a step that ARRIVED BY EVENT to a care episode (ADR-0031).
///
/// <para>Split from the RabbitMQ consumer on purpose. Everything that can be got wrong here — resolving the
/// episode, refusing an encounter we do not own, surviving a redelivery — is decided in this class, which a
/// test can drive against a real database with no broker in the room. What is left in the consumer is
/// transport: connect, ack, nack.</para>
///
/// <para><b>The appointment and the member come from OUR encounter row, never from the payload.</b>
/// orders-service and pharmacy-service both put <c>beneficiaryId</c> on the wire and both are perfectly
/// truthful about it — but emr owns encounters, so emr is the only service that can be WRONG about which
/// member a visit is for, and a timeline that trusted a sibling's copy would show the sibling's staleness as
/// this patient's history. The payload names the encounter; everything else is looked up.</para>
///
/// <para><b>An unknown encounter is acked, not retried.</b> Under RLS a row belonging to another tenant is
/// indistinguishable from one that does not exist, and neither improves by being redelivered. Nacking would
/// put a message that is permanently unusable into a dead-letter queue and, if requeued, into a hot loop.</para>
/// </summary>
public sealed class CareEpisodeAppender(EmrDbContext db, CareTimelineWriter timeline)
{
    public async Task<CareStepOutcome> AppendAsync(
        CareStepDraft draft, Guid eventId, DateTimeOffset occurredAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        // Cheap pre-check for the ordinary redelivery. The unique index below is what actually guarantees it —
        // two deliveries can be in flight at once (the consumer takes a prefetch window) and a read cannot see
        // an insert that has not committed.
        if (await db.CareTimeline.AsNoTracking().AnyAsync(s => s.EventId == eventId, ct))
            return CareStepOutcome.Duplicate;

        var episode = await db.Encounters.AsNoTracking()
            .Where(e => e.EncounterId == draft.EncounterId)
            .Select(e => new { e.BeneficiaryId, e.AppointmentId })
            .FirstOrDefaultAsync(ct);
        if (episode is null) return CareStepOutcome.UnknownEncounter;

        timeline.Add(
            draft.Step, episode.BeneficiaryId,
            encounterId: draft.EncounterId, appointmentId: episode.AppointmentId,
            actor: draft.Actor, reference: draft.Reference,
            source: draft.Source, eventId: eventId, occurredAt: occurredAt);

        try
        {
            await db.SaveChangesAsync(ct);
            return CareStepOutcome.Appended;
        }
        catch (DbUpdateException ex) when (IsDuplicateEvent(ex))
        {
            // A concurrent delivery of the same event won the race. The step exists, which is the outcome we
            // wanted; the loser must forget its own copy or the next SaveChanges on this context would retry
            // the same doomed insert.
            db.ChangeTracker.Clear();
            return CareStepOutcome.Duplicate;
        }
    }

    /// <summary>The <c>ux_care_timeline_event</c> partial unique index firing (migration 0019) — at-least-once
    /// delivery arriving twice, which is the mechanism working rather than a fault. Matched on the constraint
    /// NAME as well as the SQLSTATE: 23505 on any other index here would be a real bug, and swallowing it as
    /// "seen this already" would hide it.</summary>
    private static bool IsDuplicateEvent(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: "23505" } pg &&
        string.Equals(pg.ConstraintName, "ux_care_timeline_event", StringComparison.Ordinal);
}
