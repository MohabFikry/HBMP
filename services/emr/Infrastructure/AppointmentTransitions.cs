using Mersal.Emr.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Mersal.Emr.Infrastructure;

/// <summary>Idempotency ledger row (emr.processed_request): a replayed Idempotency-Key returns the prior
/// outcome instead of re-applying a transition.</summary>
public sealed class ProcessedRequest
{
    public string IdempotencyKey { get; set; } = default!;
    public string Operation { get; set; } = default!;
    public Guid? AppointmentId { get; set; }
    public int StatusCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum TransitionOutcome { Ok, NotFound, IllegalTransition, SlotTaken, SlotNotFound, PreconditionFailed }

/// <summary>Result of a reschedule/cancel/no-show. <see cref="Promoted"/> is the waitlist entry promoted by a
/// freed slot (cancel/no-show); <see cref="NoShowCount"/> is the beneficiary's running no-show tally.</summary>
public readonly record struct TransitionResult(
    TransitionOutcome Outcome, Appointment? Appointment, WaitlistEntry? Promoted = null, int NoShowCount = 0)
{
    public static TransitionResult Fail(TransitionOutcome o) => new(o, null);
}

/// <summary>Appointment state transitions (3.2, 23-state-machines §6). Every transition routes its legality
/// through <see cref="AppointmentWorkflow"/> so illegal moves are rejected uniformly (surfaced as an audited
/// 409). Reschedule is atomic — the new slot is acquired concurrency-safely and the old slot is released in
/// ONE transaction, so a reschedule never leaves both slots held or both free.</summary>
public sealed class AppointmentTransitionService(EmrDbContext db)
{
    /// <summary>Optimistic concurrency: stamp the client's If-Match (xmin) as the row's original version so a
    /// concurrent write is caught on save (→ PreconditionFailed / HTTP 412).</summary>
    private void ApplyIfMatch(Appointment appt, uint? ifMatch)
    {
        if (ifMatch is { } v) db.Entry(appt).Property(x => x.RowVersion).OriginalValue = v;
    }

    public async Task<TransitionResult> RescheduleAsync(
        Guid appointmentId, Guid newSlotId, uint? ifMatch, DateTimeOffset now, CancellationToken ct = default)
    {
        var appt = await db.Appointments.FirstOrDefaultAsync(a => a.AppointmentId == appointmentId, ct);
        if (appt is null) return TransitionResult.Fail(TransitionOutcome.NotFound);
        if (!AppointmentWorkflow.CanReschedule(appt.Status)) return TransitionResult.Fail(TransitionOutcome.IllegalTransition);
        if (appt.SlotId == newSlotId) return new TransitionResult(TransitionOutcome.Ok, appt); // no-op move

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            var npgTx = (NpgsqlTransaction)tx.GetDbTransaction();

            await using (var lockCmd = conn.CreateCommand())
            {
                lockCmd.Transaction = npgTx;
                lockCmd.CommandText = "SELECT 1 FROM emr.appointment_slot WHERE slot_id = @s FOR UPDATE";
                lockCmd.Parameters.AddWithValue("s", newSlotId);
                if (await lockCmd.ExecuteScalarAsync(ct) is null) { await tx.RollbackAsync(ct); return TransitionResult.Fail(TransitionOutcome.SlotNotFound); }
            }
            await using (var takenCmd = conn.CreateCommand())
            {
                takenCmd.Transaction = npgTx;
                takenCmd.CommandText = "SELECT 1 FROM emr.appointment WHERE slot_id = @s AND status IN ('Booked','CheckedIn') LIMIT 1";
                takenCmd.Parameters.AddWithValue("s", newSlotId);
                if (await takenCmd.ExecuteScalarAsync(ct) is not null) { await tx.RollbackAsync(ct); return TransitionResult.Fail(TransitionOutcome.SlotTaken); }
            }

            var newSlot = await db.AppointmentSlots.AsNoTracking().FirstAsync(s => s.SlotId == newSlotId, ct);
            appt.SlotId = newSlotId;              // releasing the old slot is implicit (nothing else holds it)
            appt.ScheduledStart = newSlot.SlotStart;
            appt.ScheduledEnd = newSlot.SlotEnd;
            appt.UpdatedAt = now;
            ApplyIfMatch(appt, ifMatch);
            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new TransitionResult(TransitionOutcome.Ok, appt);
            }
            catch (DbUpdateConcurrencyException) { await tx.RollbackAsync(ct); return TransitionResult.Fail(TransitionOutcome.PreconditionFailed); }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            { await tx.RollbackAsync(ct); return TransitionResult.Fail(TransitionOutcome.SlotTaken); }
        });
    }

    public async Task<TransitionResult> CancelAsync(
        Guid appointmentId, string? reason, uint? ifMatch, DateTimeOffset now, CancellationToken ct = default)
    {
        var appt = await db.Appointments.FirstOrDefaultAsync(a => a.AppointmentId == appointmentId, ct);
        if (appt is null) return TransitionResult.Fail(TransitionOutcome.NotFound);
        if (!AppointmentWorkflow.CanCancel(appt.Status)) return TransitionResult.Fail(TransitionOutcome.IllegalTransition);

        var freedSlot = appt.SlotId is not null;
        appt.Status = AppointmentStatus.Cancelled;
        appt.CancelReason = reason;
        appt.UpdatedAt = now;
        ApplyIfMatch(appt, ifMatch);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return TransitionResult.Fail(TransitionOutcome.PreconditionFailed); }

        var promoted = freedSlot ? await PromoteWaitlistAsync(appt, now, ct) : null;
        return new TransitionResult(TransitionOutcome.Ok, appt, promoted);
    }

    public async Task<TransitionResult> NoShowAsync(
        Guid appointmentId, uint? ifMatch, DateTimeOffset now, TimeSpan grace, CancellationToken ct = default)
    {
        var appt = await db.Appointments.FirstOrDefaultAsync(a => a.AppointmentId == appointmentId, ct);
        if (appt is null) return TransitionResult.Fail(TransitionOutcome.NotFound);
        if (!AppointmentWorkflow.CanNoShow(appt, now, grace)) return TransitionResult.Fail(TransitionOutcome.IllegalTransition);

        appt.Status = AppointmentStatus.NoShow;
        appt.NoShow = true;                       // reporting flag (US-022)
        appt.UpdatedAt = now;
        ApplyIfMatch(appt, ifMatch);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return TransitionResult.Fail(TransitionOutcome.PreconditionFailed); }

        var promoted = await PromoteWaitlistAsync(appt, now, ct);   // free the slot for backfill
        var noShowCount = await db.Appointments.CountAsync(
            a => a.BeneficiaryId == appt.BeneficiaryId && a.Status == AppointmentStatus.NoShow, ct);
        return new TransitionResult(TransitionOutcome.Ok, appt, promoted, noShowCount);
    }

    /// <summary>Promote the earliest waiting entry for the freed provider/location (23 §6 Waitlisted→Scheduled).
    /// Marks it Promoted; the appointment team completes the re-booking. Null if the waitlist is empty.</summary>
    private async Task<WaitlistEntry?> PromoteWaitlistAsync(Appointment freed, DateTimeOffset now, CancellationToken ct)
    {
        var next = await db.WaitlistEntries
            .Where(w => w.ProviderId == freed.ProviderId && w.LocationId == freed.LocationId
                        && w.Status == WaitlistStatus.Waitlisted)
            .OrderByDescending(w => w.PriorityScore).ThenBy(w => w.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (next is null) return null;
        next.Status = WaitlistStatus.Promoted;
        await db.SaveChangesAsync(ct);
        return next;
    }
}

/// <summary>Idempotency ledger for mutating endpoints. A seen key short-circuits with the prior status.</summary>
public sealed class IdempotencyStore(EmrDbContext db)
{
    public Task<ProcessedRequest?> FindAsync(string key, CancellationToken ct = default) =>
        db.Set<ProcessedRequest>().AsNoTracking().FirstOrDefaultAsync(p => p.IdempotencyKey == key, ct)!;

    public async Task RecordAsync(string key, string operation, Guid? appointmentId, int statusCode, CancellationToken ct = default)
    {
        db.Set<ProcessedRequest>().Add(new ProcessedRequest
        {
            IdempotencyKey = key, Operation = operation, AppointmentId = appointmentId,
            StatusCode = statusCode, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}
