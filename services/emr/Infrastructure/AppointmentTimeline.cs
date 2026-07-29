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
                SELECT row_snapshot ->> 'status'     AS status,
                       changed_at                    AS changed_at,
                       row_snapshot ->> 'updated_by' AS updated_by,
                       row_snapshot ->> 'created_by' AS created_by
                  FROM emr.appointment_history
                 WHERE appointment_id = {0}
                 ORDER BY history_id
                """, appointmentId)
            .ToListAsync(ct);

        return Collapse(raw);
    }

    /// <summary>Pure: keep the first snapshot and every later one whose status differs from the one before it.
    /// Attribution falls back to created_by for the opening step, which is the only one it describes.</summary>
    public static List<TimelineRow> Collapse(IReadOnlyList<HistoryProjection> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var steps = new List<TimelineRow>();
        string? previous = null;
        for (var i = 0; i < snapshots.Count; i++)
        {
            var snap = snapshots[i];
            if (string.IsNullOrWhiteSpace(snap.Status)) continue;
            if (i > 0 && snap.Status == previous) continue;
            steps.Add(new TimelineRow(snap.Status, snap.ChangedAt, steps.Count == 0 ? snap.CreatedBy : snap.UpdatedBy));
            previous = snap.Status;
        }
        return steps;
    }

    /// <summary>Raw shape read out of the history snapshot (public so the collapse rule is testable).</summary>
    public sealed record HistoryProjection(string? Status, DateTimeOffset ChangedAt, string? UpdatedBy, string? CreatedBy);
}
