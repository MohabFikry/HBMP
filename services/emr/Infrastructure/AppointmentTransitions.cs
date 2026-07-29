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

    /// <summary>Check a beneficiary in (Booked→CheckedIn) and place a min-necessary ticket on the reception
    /// queue (3.3). Transition legality goes through <see cref="AppointmentWorkflow"/>.</summary>
    public async Task<TransitionResult> CheckInAsync(
        Guid appointmentId, string? memberNo, string? displayName, int priority, uint? ifMatch,
        DateTimeOffset now, CancellationToken ct = default)
    {
        var appt = await db.Appointments.FirstOrDefaultAsync(a => a.AppointmentId == appointmentId, ct);
        if (appt is null) return TransitionResult.Fail(TransitionOutcome.NotFound);
        if (!AppointmentWorkflow.CanTransition(appt.Status, AppointmentStatus.CheckedIn))
            return TransitionResult.Fail(TransitionOutcome.IllegalTransition);

        appt.Status = AppointmentStatus.CheckedIn;
        appt.UpdatedAt = now;
        ApplyIfMatch(appt, ifMatch);
        db.Set<QueueTicket>().Add(new QueueTicket
        {
            QueueId = Guid.NewGuid(), AppointmentId = appt.AppointmentId, BeneficiaryId = appt.BeneficiaryId,
            ProviderId = appt.ProviderId, LocationId = appt.LocationId,
            // The ticket inherits the appointment's branch. GET /queues filters on exactly this column for a
            // BranchScoped caller, so a ticket without it is invisible to the desk that just created it.
            BranchId = appt.BranchId,
            MemberNo = memberNo, DisplayName = displayName, AppointmentType = appt.AppointmentType,
            Priority = priority, State = QueueTicketState.Waiting, EnqueuedAt = now,
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return TransitionResult.Fail(TransitionOutcome.PreconditionFailed); }
        return new TransitionResult(TransitionOutcome.Ok, appt);
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

    /// <summary>
    /// 18.A3: cancel is ONE transaction. It used to be three unwrapped SaveChanges (status, queue
    /// tickets, waitlist promotion), so a failure between them left an appointment cancelled with a
    /// live queue ticket, or a waitlist entry promoted against a cancel that never landed.
    /// </summary>
    public async Task<TransitionResult> CancelAsync(
        Guid appointmentId, string? reason, uint? ifMatch, DateTimeOffset now, CancellationToken ct = default)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            var appt = await db.Appointments.FirstOrDefaultAsync(a => a.AppointmentId == appointmentId, ct);
            if (appt is null) return TransitionResult.Fail(TransitionOutcome.NotFound);
            if (!AppointmentWorkflow.CanCancel(appt.Status)) return TransitionResult.Fail(TransitionOutcome.IllegalTransition);

            var freedSlot = appt.SlotId is not null;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            appt.Status = AppointmentStatus.Cancelled;
            appt.CancelReason = reason;
            appt.UpdatedAt = now;
            ApplyIfMatch(appt, ifMatch);
            MarkQueueTicketsRemoved(await ActiveQueueTicketsAsync(appointmentId, ct));   // cancel clears the queue (3.3)

            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException)
            {
                await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
                return TransitionResult.Fail(TransitionOutcome.PreconditionFailed);
            }

            var promoted = freedSlot ? await PromoteWaitlistAsync(appt, ct) : null;
            await tx.CommitAsync(ct);
            return new TransitionResult(TransitionOutcome.Ok, appt, promoted);
        });
    }

    /// <summary>18.A3: no-show is ONE transaction, for the same reason as <see cref="CancelAsync"/>.</summary>
    public async Task<TransitionResult> NoShowAsync(
        Guid appointmentId, uint? ifMatch, DateTimeOffset now, TimeSpan grace, CancellationToken ct = default)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            var appt = await db.Appointments.FirstOrDefaultAsync(a => a.AppointmentId == appointmentId, ct);
            if (appt is null) return TransitionResult.Fail(TransitionOutcome.NotFound);
            if (!AppointmentWorkflow.CanNoShow(appt, now, grace)) return TransitionResult.Fail(TransitionOutcome.IllegalTransition);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            appt.Status = AppointmentStatus.NoShow;
            appt.NoShow = true;                       // reporting flag (US-022)
            appt.UpdatedAt = now;
            ApplyIfMatch(appt, ifMatch);
            MarkQueueTicketsRemoved(await ActiveQueueTicketsAsync(appointmentId, ct));   // no-show clears the queue (3.3)

            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException)
            {
                await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
                return TransitionResult.Fail(TransitionOutcome.PreconditionFailed);
            }

            var promoted = await PromoteWaitlistAsync(appt, ct);   // free the slot for backfill
            var noShowCount = await db.Appointments.CountAsync(
                a => a.BeneficiaryId == appt.BeneficiaryId && a.Status == AppointmentStatus.NoShow, ct);
            await tx.CommitAsync(ct);
            return new TransitionResult(TransitionOutcome.Ok, appt, promoted, noShowCount);
        });
    }

    /// <summary>
    /// Promote the earliest waiting entry for the freed provider/location (23 §6 Waitlisted→Scheduled).
    /// Marks it Promoted; the appointment team completes the re-booking. Null if the waitlist is empty.
    ///
    /// 18.A3: the row is claimed with <c>FOR UPDATE SKIP LOCKED</c> inside the caller's transaction and
    /// the status change is a guarded UPDATE. Unlocked, two concurrent cancels both read the SAME head
    /// of the queue and promoted it twice — one freed slot, two people told they had it, and the second
    /// waiting beneficiary silently skipped. SKIP LOCKED means the second cancel takes the NEXT entry
    /// instead of blocking on the first.
    /// </summary>
    private async Task<WaitlistEntry?> PromoteWaitlistAsync(Appointment freed, CancellationToken ct)
    {
        var claimed = await db.WaitlistEntries.FromSqlRaw(
            """
            SELECT * FROM emr.waitlist_entry
            WHERE provider_id = {0} AND location_id = {1} AND status = 'Waitlisted'
            ORDER BY priority_score DESC, created_at ASC
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """, freed.ProviderId, freed.LocationId).FirstOrDefaultAsync(ct);
        if (claimed is null) return null;

        // Guarded write: even if the lock were somehow not held, the status predicate makes the
        // promotion a compare-and-set rather than a blind overwrite.
        var affected = await db.WaitlistEntries
            .Where(w => w.WaitlistId == claimed.WaitlistId && w.Status == WaitlistStatus.Waitlisted)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, WaitlistStatus.Promoted), ct);
        if (affected == 0) return null;

        claimed.Status = WaitlistStatus.Promoted;
        return claimed;
    }

    /// <summary>Active (Waiting/InConsultation) queue tickets for an appointment (3.3).</summary>
    private Task<List<QueueTicket>> ActiveQueueTicketsAsync(Guid appointmentId, CancellationToken ct) =>
        db.Set<QueueTicket>()
            .Where(t => t.AppointmentId == appointmentId
                        && (t.State == QueueTicketState.Waiting || t.State == QueueTicketState.InConsultation))
            .ToListAsync(ct);

    /// <summary>Mark tickets removed in the change tracker — saved with the transition, never separately,
    /// so the reception queue can never disagree with the appointment's status.</summary>
    private static void MarkQueueTicketsRemoved(List<QueueTicket> tickets)
    {
        foreach (var t in tickets) t.State = QueueTicketState.Removed;
    }
}

/// <summary>Idempotency ledger for mutating endpoints. A seen key short-circuits with the prior status.</summary>
public sealed class IdempotencyStore(EmrDbContext db, TimeProvider clock)
{
    public Task<ProcessedRequest?> FindAsync(string key, CancellationToken ct = default) =>
        db.Set<ProcessedRequest>().AsNoTracking().FirstOrDefaultAsync(p => p.IdempotencyKey == key, ct)!;

    public async Task RecordAsync(string key, string operation, Guid? appointmentId, int statusCode, CancellationToken ct = default)
    {
        db.Set<ProcessedRequest>().Add(new ProcessedRequest
        {
            IdempotencyKey = key, Operation = operation, AppointmentId = appointmentId,
            StatusCode = statusCode, CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
    }
}
