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
        DateTimeOffset now, string? actor = null, CancellationToken ct = default)
    {
        var appt = await db.Appointments.FirstOrDefaultAsync(a => a.AppointmentId == appointmentId, ct);
        if (appt is null) return TransitionResult.Fail(TransitionOutcome.NotFound);
        if (!AppointmentWorkflow.CanTransition(appt.Status, AppointmentStatus.CheckedIn))
            return TransitionResult.Fail(TransitionOutcome.IllegalTransition);

        appt.Status = AppointmentStatus.CheckedIn;
        appt.UpdatedAt = now;
        appt.UpdatedBy = actor;
        // 30.5c — the DURABLE arrival moment. updated_at is overwritten by every later transition, so it
        // could never answer "how long did this person wait"; this column can (design 46 §7c). Set only on
        // the first check-in, so a re-check-in cannot move an arrival that already happened.
        appt.CheckedInAt ??= now;
        appt.CheckedInBy ??= actor;
        // Backfill the name for an appointment booked before 0013 carried one. `??=` deliberately: the name
        // the appointment was BOOKED under wins, because that is what the patient was told and what the desk's
        // list has been showing all morning — check-in must not quietly rewrite it.
        if (string.IsNullOrWhiteSpace(appt.BeneficiaryName) && !string.IsNullOrWhiteSpace(displayName))
            appt.BeneficiaryName = displayName.Trim();
        ApplyIfMatch(appt, ifMatch);
        db.Set<QueueTicket>().Add(new QueueTicket
        {
            QueueId = Guid.NewGuid(), AppointmentId = appt.AppointmentId, BeneficiaryId = appt.BeneficiaryId,
            // 24.x — THE TICKET'S TENANT IS THE APPOINTMENT'S, and it is set here rather than left to the
            // ambient stamper. The stamper fills a blank from RlsContext, which is empty on any path that
            // does not run through UseHbmpRls — and a ticket written with tenant_id = '' belongs to nobody:
            // invisible to every real tenant, so the person waiting simply disappears from the board. One
            // such row was found on the dev database. The appointment is the authoritative source anyway;
            // asking an ambient value for something the row already knows is how it went missing.
            TenantId = appt.TenantId,
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

    /// <param name="insideTransaction">24.x — run INSIDE this transaction, immediately before it commits, so
    /// the domain event and the state change it announces are one fact or neither. The endpoint used to
    /// enqueue after this returned, which is a second commit: a crash in between leaves a slot freed or a
    /// booking moved with nothing downstream told. A callback rather than an outer transaction because this
    /// runs under an execution strategy that may RETRY the delegate — a retry re-enqueues inside the new
    /// transaction, which is right, where an outer transaction would have committed the first attempt's.</param>
    public async Task<TransitionResult> RescheduleAsync(
        Guid appointmentId, Guid newSlotId, uint? ifMatch, DateTimeOffset now, string? actor = null,
        Func<Appointment, CancellationToken, Task>? insideTransaction = null, CancellationToken ct = default)
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
            // THE DOCTOR MOVES WITH THE SLOT.
            //
            // It did not, and the omission was invisible while the only way to reschedule was to pick another
            // slot belonging to the same doctor — the UI filtered the picker by the appointment's own doctor,
            // so the two could never disagree. The moment a desk can move a patient to a DIFFERENT
            // practitioner, an appointment left pointing at its old doctor while sitting in a new doctor's
            // slot is a row that contradicts itself: the board names one clinician and the session belongs to
            // another, and the patient is called by neither.
            //
            // Assigned rather than coalesced: a slot with no doctor is a clinic-level session, and inheriting
            // the previous doctor onto it would assert a clinician who is not the one holding the slot.
            appt.DoctorId = newSlot.DoctorId;
            appt.UpdatedAt = now;
            appt.UpdatedBy = actor;
            ApplyIfMatch(appt, ifMatch);
            try
            {
                await db.SaveChangesAsync(ct);
                // ONCE. This line was written twice, so every reschedule enqueued `ApptRescheduled` twice —
                // and consumer dedupe could not save it, because each enqueue mints its own event id, so the
                // two are indistinguishable from two genuine reschedules to every subscriber downstream.
                if (insideTransaction is not null) await insideTransaction(appt, ct);
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
    /// <param name="insideTransaction">24.x — run INSIDE this transaction, immediately before it commits, so
    /// the domain event and the state change it announces are one fact or neither. The endpoint used to
    /// enqueue after this returned, which is a second commit: a crash in between leaves a slot freed or a
    /// booking moved with nothing downstream told. A callback rather than an outer transaction because this
    /// runs under an execution strategy that may RETRY the delegate — a retry re-enqueues inside the new
    /// transaction, which is right, where an outer transaction would have committed the first attempt's.</param>
    public async Task<TransitionResult> CancelAsync(
        Guid appointmentId, string? reason, uint? ifMatch, DateTimeOffset now, string? actor = null,
        Func<Appointment, WaitlistEntry?, CancellationToken, Task>? insideTransaction = null, CancellationToken ct = default)
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
            appt.UpdatedBy = actor;
            ApplyIfMatch(appt, ifMatch);
            MarkQueueTicketsRemoved(await ActiveQueueTicketsAsync(appointmentId, ct));   // cancel clears the queue (3.3)

            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException)
            {
                await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
                return TransitionResult.Fail(TransitionOutcome.PreconditionFailed);
            }

            var promoted = freedSlot ? await PromoteWaitlistAsync(appt, ct) : null;
            if (insideTransaction is not null) await insideTransaction(appt, promoted, ct);
            await tx.CommitAsync(ct);
            return new TransitionResult(TransitionOutcome.Ok, appt, promoted);
        });
    }

    /// <summary>18.A3: no-show is ONE transaction, for the same reason as <see cref="CancelAsync"/>.</summary>
    /// <param name="insideTransaction">24.x — run INSIDE this transaction, immediately before it commits, so
    /// the domain event and the state change it announces are one fact or neither. The endpoint used to
    /// enqueue after this returned, which is a second commit: a crash in between leaves a slot freed or a
    /// booking moved with nothing downstream told. A callback rather than an outer transaction because this
    /// runs under an execution strategy that may RETRY the delegate — a retry re-enqueues inside the new
    /// transaction, which is right, where an outer transaction would have committed the first attempt's.</param>
    public async Task<TransitionResult> NoShowAsync(
        Guid appointmentId, uint? ifMatch, DateTimeOffset now, TimeSpan grace, string? actor = null,
        Func<Appointment, int, WaitlistEntry?, CancellationToken, Task>? insideTransaction = null, CancellationToken ct = default)
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
            appt.UpdatedBy = actor;
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
            // The tally is passed in because the repeat-no-show event depends on it, and it is only
            // knowable here — inside the transaction, after the status write that changes it.
            if (insideTransaction is not null) await insideTransaction(appt, noShowCount, promoted, ct);
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
