using Mersal.Emr.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Mersal.Emr.Infrastructure;

/// <summary>Outcome of a booking attempt.</summary>
public enum BookOutcome { Booked, SlotTaken, SlotNotFound, DailyCapacityReached }

public readonly record struct BookResult(BookOutcome Outcome, Appointment? Appointment, int Booked = 0, int Cap = 0)
{
    public static BookResult Ok(Appointment a) => new(BookOutcome.Booked, a);
    public static readonly BookResult SlotTaken = new(BookOutcome.SlotTaken, null);
    public static readonly BookResult SlotNotFound = new(BookOutcome.SlotNotFound, null);
    /// <summary>Carries the two numbers, because "full" without them is not something an operator can act on:
    /// "18 of 18" tells them to look for another day, and it also tells them the cap is 18 when they thought
    /// it was 20.</summary>
    public static BookResult CapacityReached(int booked, int cap) =>
        new(BookOutcome.DailyCapacityReached, null, booked, cap);
}

/// <summary>
/// 0025 — the daily cap, checked at BOOKING as well as at generation (design 42 §7 rule 13).
///
/// <para>Generation alone is not enough. A walk-in is slotless, an ad-hoc clinic adds windows the rule never
/// described, and the call-centre façade books by naming a doctor. Each of those reaches an appointment
/// without consuming a materialized slot, so a cap that only shapes the calendar is not a cap for any of
/// them.</para>
/// </summary>
public readonly record struct DailyCapacityCheck(Guid DoctorId, DateOnly ClinicDate, int Cap);

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
    /// <param name="capacity">0025 — the practitioner's daily cap, when they have one. Checked INSIDE the
    /// transaction under a per-(doctor, day) advisory lock; see <see cref="RefusedForCapacityAsync"/>.</param>
    public async Task<BookResult> BookAsync(Appointment appt,
        Func<Appointment, CancellationToken, Task>? insideTransaction = null,
        DailyCapacityCheck? capacity = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(appt);

        // Idempotent replay → return the prior appointment, do not create a second.
        if (appt.IdempotencyKey is { Length: > 0 } idem)
        {
            var prior = await db.Appointments.AsNoTracking()
                .FirstOrDefaultAsync(a => a.IdempotencyKey == idem, ct);
            if (prior is not null) return BookResult.Ok(prior);
        }

        // Slotless walk-ins skip the FOR UPDATE lock (no shared resource to contend on) — UNLESS a cap
        // applies, in which case the day's count IS the shared resource and needs the same protection.
        if (appt.SlotId is null && capacity is null)
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
            if (appt.SlotId is { } slotId)
            {
                await using (var lockCmd = conn.CreateCommand())
                {
                    lockCmd.Transaction = (NpgsqlTransaction)tx.GetDbTransaction();
                    lockCmd.CommandText = "SELECT 1 FROM emr.appointment_slot WHERE slot_id = @s FOR UPDATE";
                    lockCmd.Parameters.AddWithValue("s", slotId);
                    var found = await lockCmd.ExecuteScalarAsync(ct);
                    if (found is null) { await tx.RollbackAsync(ct); return BookResult.SlotNotFound; }
                }

                // (2) Reject if an active appointment already holds the slot (fast path before the unique index).
                await using var takenCmd = conn.CreateCommand();
                takenCmd.Transaction = (NpgsqlTransaction)tx.GetDbTransaction();
                takenCmd.CommandText =
                    "SELECT 1 FROM emr.appointment WHERE slot_id = @s AND status IN ('Booked','CheckedIn') LIMIT 1";
                takenCmd.Parameters.AddWithValue("s", slotId);
                if (await takenCmd.ExecuteScalarAsync(ct) is not null)
                {
                    await tx.RollbackAsync(ct);
                    return BookResult.SlotTaken;
                }
            }

            // (3) The daily cap.
            if (capacity is { } cap && await RefusedForCapacityAsync(conn, tx, cap, ct) is { } full)
            {
                await tx.RollbackAsync(ct);
                return full;
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

    /// <summary>
    /// Is this practitioner already at capacity for the day? Null when there is room.
    ///
    /// <para><b>The advisory lock is the load-bearing part, and it is not optional.</b> The slot lock above
    /// serializes two people booking THE SAME slot. It does nothing for two people booking two DIFFERENT
    /// slots on the same doctor's last remaining place: each transaction counts 19, each sees room, both
    /// insert, and the doctor arrives to 21 patients against a cap of 20. Counting inside a transaction is
    /// not the same as counting under a lock.</para>
    ///
    /// <para><c>pg_advisory_xact_lock</c> keyed on (doctor, day) makes those two bookers queue exactly as the
    /// slot lock makes same-slot bookers queue, and it is released by the commit or rollback — there is no
    /// path where a crash leaves a doctor's day locked. It is a transaction-scoped lock rather than a row
    /// lock because the thing being protected is a COUNT, which no single row represents.</para>
    ///
    /// <para>Statuses counted are the live ones — Booked and CheckedIn — matching
    /// <c>ux_appointment_active_slot</c>. A cancelled appointment gives its place back, which is what a
    /// coordinator ringing round to rearrange a day is relying on.</para>
    /// </summary>
    private static async Task<BookResult?> RefusedForCapacityAsync(
        NpgsqlConnection conn, IDbContextTransaction tx, DailyCapacityCheck capacity, CancellationToken ct)
    {
        await using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.Transaction = (NpgsqlTransaction)tx.GetDbTransaction();
            lockCmd.CommandText = "SELECT pg_advisory_xact_lock(@k)";
            lockCmd.Parameters.AddWithValue("k", AdvisoryKey(capacity.DoctorId, capacity.ClinicDate));
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        await using var countCmd = conn.CreateCommand();
        countCmd.Transaction = (NpgsqlTransaction)tx.GetDbTransaction();
        // The clinic's day, in Cairo — the same conversion every other date comparison on this service makes.
        // Counting on the UTC date would put a 23:30 Cairo appointment on the following day's tally and let
        // one clinic overrun its cap for the two hours every evening the two calendars disagree.
        countCmd.CommandText =
            """
            SELECT count(*) FROM emr.appointment
             WHERE doctor_id = @d
               AND status IN ('Booked','CheckedIn')
               AND (scheduled_start AT TIME ZONE 'Africa/Cairo')::date = @day
            """;
        countCmd.Parameters.AddWithValue("d", capacity.DoctorId);
        countCmd.Parameters.AddWithValue("day", capacity.ClinicDate.ToDateTime(TimeOnly.MinValue));

        var booked = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct) ?? 0);
        return booked >= capacity.Cap ? BookResult.CapacityReached(booked, capacity.Cap) : null;
    }

    /// <summary>
    /// A stable 64-bit key for the (doctor, day) advisory lock.
    ///
    /// <para>Derived from the GUID's own bytes and the day number, NOT from <c>string.GetHashCode</c>: .NET
    /// randomizes string hashing per process, so two API instances would compute different keys for the same
    /// doctor and the same day, take different locks, and serialize nothing at all — while every local test
    /// on a single process passed.</para>
    /// </summary>
    public static long AdvisoryKey(Guid doctorId, DateOnly day)
    {
        Span<byte> bytes = stackalloc byte[16];
        doctorId.TryWriteBytes(bytes);
        var hi = BitConverter.ToInt64(bytes[..8]);
        var lo = BitConverter.ToInt64(bytes[8..]);
        return hi ^ lo ^ ((long)day.DayNumber << 17);
    }
}
