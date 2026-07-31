using Mersal.Emr.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Mersal.Emr.Infrastructure;

/// <summary>Outcome of a booking attempt.</summary>
public enum BookOutcome { Booked, SlotTaken, SlotNotFound }

public readonly record struct BookResult(BookOutcome Outcome, Appointment? Appointment)
{
    public static BookResult Ok(Appointment a) => new(BookOutcome.Booked, a);
    public static readonly BookResult SlotTaken = new(BookOutcome.SlotTaken, null);
    public static readonly BookResult SlotNotFound = new(BookOutcome.SlotNotFound, null);
}

/// <summary>Concurrency-safe slot booking (3.1). Two coordinators must NEVER double-book one slot; this is
/// guaranteed in depth: (1) inside a serializable-enough transaction the slot row is locked
/// <c>FOR UPDATE</c> so concurrent bookers queue, and any existing active hold is detected; (2) the
/// <c>ux_appointment_active_slot</c> partial-unique index is the datastore backstop — the loser's INSERT
/// raises <c>23505</c>, surfaced as <see cref="BookOutcome.SlotTaken"/> (HTTP 409). Never a double-book.</summary>
public sealed class AppointmentBookingService(EmrDbContext db)
{
    /// <param name="insideTransaction">24.x — run INSIDE the booking transaction, immediately before it
    /// commits, so the appointment and the event announcing it are one fact or neither. The endpoint
    /// used to enqueue after this returned: a crash in between held the slot with nothing downstream
    /// told, which is a patient booked into a slot no board shows. A callback rather than an outer
    /// transaction because this runs under an execution strategy that may RETRY — a retry re-enqueues
    /// inside the new transaction, where an outer one would have kept the abandoned attempt's event.</param>
    public async Task<BookResult> BookAsync(Appointment appt,
        Func<Appointment, CancellationToken, Task>? insideTransaction = null, CancellationToken ct = default)
    {
        // Idempotent replay → return the prior appointment, do not create a second.
        if (appt.IdempotencyKey is { Length: > 0 } idem)
        {
            var prior = await db.Appointments.AsNoTracking()
                .FirstOrDefaultAsync(a => a.IdempotencyKey == idem, ct);
            if (prior is not null) return BookResult.Ok(prior);
        }

        // Slotless walk-ins skip the FOR UPDATE lock (no shared resource to contend on).
        if (appt.SlotId is null)
        {
            db.Appointments.Add(appt);
            await db.SaveChangesAsync(ct);
            return BookResult.Ok(appt);
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();

            // (1) Lock the slot row; concurrent bookers of the same slot serialize here.
            await using (var lockCmd = conn.CreateCommand())
            {
                lockCmd.Transaction = (NpgsqlTransaction)tx.GetDbTransaction();
                lockCmd.CommandText = "SELECT 1 FROM emr.appointment_slot WHERE slot_id = @s FOR UPDATE";
                lockCmd.Parameters.AddWithValue("s", appt.SlotId!.Value);
                var found = await lockCmd.ExecuteScalarAsync(ct);
                if (found is null) { await tx.RollbackAsync(ct); return BookResult.SlotNotFound; }
            }

            // (2) Reject if an active appointment already holds the slot (fast path before the unique index).
            await using (var takenCmd = conn.CreateCommand())
            {
                takenCmd.Transaction = (NpgsqlTransaction)tx.GetDbTransaction();
                takenCmd.CommandText =
                    "SELECT 1 FROM emr.appointment WHERE slot_id = @s AND status IN ('Booked','CheckedIn') LIMIT 1";
                takenCmd.Parameters.AddWithValue("s", appt.SlotId!.Value);
                if (await takenCmd.ExecuteScalarAsync(ct) is not null)
                {
                    await tx.RollbackAsync(ct);
                    return BookResult.SlotTaken;
                }
            }

            try
            {
                db.Appointments.Add(appt);
                await db.SaveChangesAsync(ct);
                if (insideTransaction is not null) await insideTransaction(appt, ct);
                await tx.CommitAsync(ct);
                return BookResult.Ok(appt);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                // Backstop: the partial-unique index caught a racing insert. The other request won.
                await tx.RollbackAsync(ct);
                db.Entry(appt).State = EntityState.Detached;
                return BookResult.SlotTaken;
            }
        });
    }
}
