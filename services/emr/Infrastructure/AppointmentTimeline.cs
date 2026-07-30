using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Infrastructure;

/// <summary>One step of an appointment's operational history.</summary>
public sealed record TimelineRow(string Status, DateTimeOffset At, string? By);

/// <summary>
/// Reads an appointment's status steps out of emr.appointment_history.
///
/// The history table snapshots the ENTIRE row on every insert and update, so consecutive snapshots often share a
/// status: rescheduling changes the times, not the state. Emitting one step per snapshot would show the desk
/// "Booked, Booked, Booked" and bury the transitions that matter, so only CHANGES of status are emitted — the
/// first snapshot always, then each one whose status differs from the step before it.
/// </summary>
public static class AppointmentTimeline
{
    public static async Task<List<TimelineRow>> ReadAsync(EmrDbContext db, Guid appointmentId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Reading the two fields out of the JSONB snapshot keeps the projection in SQL: the rest of the
        // snapshot — beneficiary, provider, referral, every column — never leaves the database.
        var raw = await db.Database.SqlQueryRaw<HistoryProjection>(
                """
                -- Two separate snake_case rules meet here. LEFT of the AS: to_jsonb(NEW) keys by COLUMN name,
                -- so the snapshot's keys are snake_case rather than the entity's property names. RIGHT of it:
                -- EmrDbContext uses UseSnakeCaseNamingConvention, so SqlQueryRaw looks for snake_case columns —
                -- aliasing to "ChangedAt" makes it fail with "the required column 'changed_at' was not present".
                SELECT row_snapshot ->> 'status'           AS status,
                       changed_at                          AS changed_at,
                       row_snapshot ->> 'updated_by'       AS updated_by,
                       row_snapshot ->> 'created_by'       AS created_by,
                       row_snapshot ->> 'scheduled_start'  AS scheduled_start,
                       row_snapshot ->> 'note'             AS note
                  FROM emr.appointment_history
                 WHERE appointment_id = {0}
                 ORDER BY history_id
                """, appointmentId)
            .ToListAsync(ct);

        return Collapse(raw);
    }

    /// <summary>
    /// Pure: keep the first snapshot, every later one whose STATUS differs from the one before it, and — since
    /// 14.5 — every later one that changed something the desk actually did.
    ///
    /// <para>Collapsing purely on status was right when the only writes were transitions, and wrong the moment
    /// an appointment could be edited: rescheduling changes the times and not the state, so a reschedule left
    /// no trace at all and the timeline confidently showed "Booked → Checked in" for an appointment that had
    /// moved twice. The desk uses this to answer "why is this at 3pm when I was told 11?", which it could not.
    /// </para>
    ///
    /// <para>Consecutive identical snapshots are still suppressed — the history trigger fires on every update,
    /// including ones that touch nothing the timeline cares about, and "Booked, Booked, Booked" buries the
    /// steps that matter.</para>
    /// </summary>
    public static List<TimelineRow> Collapse(IReadOnlyList<HistoryProjection> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var steps = new List<TimelineRow>();
        HistoryProjection? previous = null;
        for (var i = 0; i < snapshots.Count; i++)
        {
            var snap = snapshots[i];
            if (string.IsNullOrWhiteSpace(snap.Status)) continue;

            if (previous is null)
            {
                steps.Add(new TimelineRow(snap.Status, snap.ChangedAt, snap.CreatedBy));
                previous = snap;
                continue;
            }

            // A status change is the headline step and keeps its own name.
            if (snap.Status != previous.Status)
                steps.Add(new TimelineRow(snap.Status, snap.ChangedAt, snap.UpdatedBy));
            // Otherwise: an EDIT. Named for what changed, because "Edited" on a reschedule does not answer
            // the question the desk opened the timeline to ask.
            else if (snap.ScheduledStart != previous.ScheduledStart)
                steps.Add(new TimelineRow(Rescheduled, snap.ChangedAt, snap.UpdatedBy));
            else if (snap.Note != previous.Note)
                steps.Add(new TimelineRow(NoteEdited, snap.ChangedAt, snap.UpdatedBy));
            else
                continue;   // nothing the timeline speaks about changed

            previous = snap;
        }
        return steps;
    }

    /// <summary>Pseudo-statuses for edits that do not move the state machine. Distinct from the
    /// <c>AppointmentStatus</c> vocabulary on purpose — they describe an ACT, not a state the row was ever in,
    /// and the UI labels them separately.</summary>
    public const string Rescheduled = "Rescheduled";
    public const string NoteEdited = "NoteEdited";

    /// <summary>Raw shape read out of the history snapshot (public so the collapse rule is testable).</summary>
    public sealed record HistoryProjection(
        string? Status, DateTimeOffset ChangedAt, string? UpdatedBy, string? CreatedBy,
        string? ScheduledStart = null, string? Note = null);
}
