using Mersal.Events;
using Mersal.Pharmacy.Domain;
using Microsoft.EntityFrameworkCore;
using Mersal.Time;

namespace Mersal.Pharmacy.Infrastructure;

/// <summary>The outcome of an atomic dispense attempt. <c>Applied</c> and <c>Replayed</c> succeed; the rest map to
/// problem responses at the edge. <c>Conflict</c> means another racer won the line's version — re-read and retry.</summary>
public enum DispenseOutcome
{
    Applied, Replayed, Conflict, NotFound, AlreadyDispensed, OverDispense, RxNotDispensable, LineNotFound, InvalidQuantity, ExpiredLot,
    /// <summary>
    /// 30.2 — THE MIRROR of the cancel path (design 46 §2). The line was CANCELLED or SUPERSEDED by the
    /// prescriber, and nothing was handed over.
    ///
    /// <para>Deliberately not folded into <see cref="AlreadyDispensed"/>. They look alike from the code's
    /// side — both mean "you may not dispense this" — and send the pharmacist to opposite places: one is a
    /// patient who already has their medicine, the other is a patient standing at the counter whose doctor
    /// withdrew it, who needs to be told why and, when it was amended, pointed at the corrected line.</para>
    /// </summary>
    LineWithdrawn,
    /// <summary>
    /// 30.x — the line belongs to a CHRONIC script and today is not inside a collectable window (design 45
    /// §5). Its own outcome because the pharmacist's answer is a DATE — "come back on the 14th" — and a
    /// generic refusal sends a beneficiary away with nothing to plan around.
    /// </summary>
    OutsideRefillWindow,
    /// <summary>18.A3 — the header is empty, over-length, or contains the reserved <c>::</c> separator.</summary>
    InvalidIdempotencyKey,
    /// <summary>18.A3 — the key was already used for a DIFFERENT dispense (changed quantity, batch or
    /// substitution). Returning the original event would tell the pharmacist a correction had been
    /// dispensed when nothing changed.</summary>
    IdempotencyKeyReuse,
}

/// <summary>
/// 30.2 — why the line was withdrawn, in the words the counter needs (design 46 §2).
///
/// <para><see cref="SupersededById"/> is the load-bearing field. Without it a pharmacist told "this was
/// amended" has no way to find the corrected line, and a patient goes home empty-handed while a perfectly
/// valid prescription sits in the system — a refusal that is technically right and operationally useless.</para>
/// </summary>
public sealed record LineWithdrawal(
    string Status, string? ReasonCode, string? ReasonText, Guid? By, DateTimeOffset? At, Guid? SupersededById);

/// <summary>30.x — why a chronic collection was refused, and WHEN it may be made instead.</summary>
public sealed record RefillRefusal(string Reason, DateOnly? OpensAt, decimal Allowed);

public sealed record DispenseResult(
    DispenseOutcome Outcome, Prescription? Prescription, DispenseEvent? Event, LineWithdrawal? Withdrawal = null,
    RefillRefusal? Refill = null)
{
    public static DispenseResult Fail(DispenseOutcome outcome, LineWithdrawal? withdrawal = null) =>
        new(outcome, null, null, withdrawal);

    public static DispenseResult RefusedRefill(RefillRefusal refusal) =>
        new(DispenseOutcome.OutsideRefillWindow, null, null, null, refusal);
}

/// <summary>The heart of phase 6 in one place (23-state-machines §3 "Pharmacy-specific guards") so the endpoint and
/// the concurrency tests exercise the SAME code — the medication analogue of phase-5's <c>ConsumeExecutor</c>. Three
/// mechanisms combine, all required:
/// <list type="number">
/// <item>append-only <c>dispense_event</c> insert per dispense, keyed by a UNIQUE idempotency key;</item>
/// <item>optimistic concurrency on the line's <c>xmin</c> — the UPDATE lands only if the line hasn't moved, so
/// exactly one of N racers wins (EF raises <see cref="DbUpdateConcurrencyException"/> for the losers);</item>
/// <item>idempotent replay — the same key returns the prior dispense_event with no new row/state change.</item>
/// </list>
/// The DB CHECK (0 ≤ dispensed ≤ prescribed) is the final backstop. Line + prescription status recompute happen in
/// one transaction; a caller may inject outbox writes via <paramref name="insideTransaction"/> so events publish
/// atomically with the state change.</summary>
public sealed class DispenseExecutor(PharmacyDbContext db)
{
    public async Task<DispenseResult> DispenseAsync(
        Guid prescriptionId, Guid lineId, string idempotencyKey, Guid dispensingPharmacyId, Guid actorId,
        decimal quantity, string batchNo, DateOnly expiryDate, Guid? substitutedDrugId, string? substitutionReason,
        string? note,
        DateTimeOffset now,
        Func<Prescription, DispenseEvent, CancellationToken, Task>? insideTransaction = null,
        CancellationToken ct = default)
    {
        if (IdempotencyKeyRules.Validate(idempotencyKey) is not null)
            return DispenseResult.Fail(DispenseOutcome.InvalidIdempotencyKey);

        var rx = await db.Prescriptions.Include(p => p.Lines).FirstOrDefaultAsync(p => p.PrescriptionId == prescriptionId, ct);
        if (rx is null) return DispenseResult.Fail(DispenseOutcome.NotFound);

        var requestHash = HashRequest(prescriptionId, lineId, quantity, batchNo, expiryDate, substitutedDrugId);

        // (3) Idempotent replay: this key already produced a dispense_event → return it unchanged, but
        // ONLY if it was the same request. A key reused with a different body is rejected (18.A3).
        var prior = await db.DispenseEvents.AsNoTracking().FirstOrDefaultAsync(d => d.IdempotencyKey == idempotencyKey, ct);
        if (prior is not null)
            return IdempotencyKeyRules.Matches(prior.RequestHash, requestHash)
                ? new DispenseResult(DispenseOutcome.Replayed, rx, prior)
                : DispenseResult.Fail(DispenseOutcome.IdempotencyKeyReuse);

        var error = Domain.Dispensing.Validate(rx, lineId, quantity, expiryDate, now);
        if (error != DispenseError.None)
        {
            // 30.2 — THE MIRROR. A withdrawn line is not "already dispensed": nothing was handed over, and
            // the pharmacist needs the reason, the prescriber and — if it was amended — where the corrected
            // line is. Answering with a generic refusal sends them to ring the doctor who already decided.
            var withdrawn = rx.Lines.FirstOrDefault(l => l.PrescriptionLineId == lineId);
            if (withdrawn is { Status: RxLineStatus.Cancelled or RxLineStatus.Superseded })
                return DispenseResult.Fail(DispenseOutcome.LineWithdrawn, new LineWithdrawal(
                    withdrawn.Status.ToString(), withdrawn.AmendmentReasonCode, withdrawn.AmendmentReasonText,
                    withdrawn.AmendedBy, withdrawn.AmendedAt, withdrawn.SupersededById));
            return DispenseResult.Fail(Map(error));
        }

        var line = rx.Lines.First(l => l.PrescriptionLineId == lineId);

        /*
         * 30.x — THE CHRONIC WINDOW GATE (design 45 §5), and the second half of the wiring phase 29 left out.
         *
         * A chronic line is metered by its SCHEDULE, not only by its total: the whole point of a refill
         * window is that a three-month script is not collectable on day one. Without this the windows were
         * decoration — rows the sweeper forfeited and nothing ever enforced.
         *
         * The COUNTER enforces and the SWEEPER records (the phase-29 window design): dispensability is
         * computed from the dates here, so a stalled sweeper delays a forfeiture and can never refuse a
         * patient standing at the counter.
         */
        var openWindow = await NextCollectableWindowAsync(rx, line, now, ct);
        if (openWindow.Refusal is { } refusal) return DispenseResult.RefusedRefill(refusal);

        line.QuantityDispensed += quantity;
        line.Status = Domain.Dispensing.RecomputeLineStatus(line);
        var evt = new DispenseEvent
        {
            DispenseId = Guid.NewGuid(), PrescriptionLineId = lineId, DispensingPharmacyId = dispensingPharmacyId,
            Quantity = quantity, IdempotencyKey = idempotencyKey, RequestHash = requestHash,
            BatchNo = batchNo, ExpiryDate = expiryDate,
            SubstitutedDrugId = substitutedDrugId, SubstitutionReason = substitutionReason,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            DispensedAt = now, DispensedBy = actorId,
        };
        db.DispenseEvents.Add(evt);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // (1)+(2) insert dispense_event + UPDATE prescription_line ... WHERE xmin=@old, atomically.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return DispenseResult.Fail(DispenseOutcome.Conflict);   // a concurrent dispense won the line's version
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            // A concurrent request with the SAME key won the insert race → idempotent: return its outcome.
            var winner = await db.DispenseEvents.AsNoTracking().FirstAsync(d => d.IdempotencyKey == idempotencyKey, ct);
            if (!IdempotencyKeyRules.Matches(winner.RequestHash, requestHash))
                return DispenseResult.Fail(DispenseOutcome.IdempotencyKeyReuse);
            var fresh = await db.Prescriptions.AsNoTracking().Include(p => p.Lines).FirstAsync(p => p.PrescriptionId == prescriptionId, ct);
            return new DispenseResult(DispenseOutcome.Replayed, fresh, winner);
        }

        // The window's own accumulator moves with the line's. Guarded on the window id, so a concurrent
        // collection against a DIFFERENT window of the same line cannot be credited to this one.
        if (openWindow.Window is { } w)
            await db.DispenseWindows.Where(x => x.WindowId == w.WindowId)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.DispensedQuantity, x => x.DispensedQuantity + quantity)
                    .SetProperty(x => x.Status, x => x.DispensedQuantity + quantity >= x.AllocatedQuantity
                        ? "Dispensed" : "PartiallyDispensed"), ct);

        // 18.A3 (audit R2 X7): recompute the prescription's status from the lines as they are NOW,
        // read back inside this transaction, and apply it with a guarded UPDATE + bounded retry. Two
        // pharmacists dispensing DIFFERENT lines used to both write PartiallyDispensed from their own
        // stale snapshot, stranding a fully-dispensed Rx so RxDispensed never emitted. The per-line xmin
        // guard above is untouched — this only fixes the roll-up.
        rx.Status = await ApplyAggregateStatusAsync(prescriptionId, ct);

        if (insideTransaction is not null) await insideTransaction(rx, evt, ct);
        await tx.CommitAsync(ct);
        return new DispenseResult(DispenseOutcome.Applied, rx, evt);
    }

    /// <summary>
    /// The window this collection belongs to, or the refusal that says why there is none.
    ///
    /// <para>An ACUTE line has no schedule and is unaffected: it returns no window and no refusal, and the
    /// dispense proceeds exactly as it always has. That is the property that makes this safe to add to a path
    /// every prescription goes through.</para>
    /// </summary>
    private async Task<(PrescriptionDispenseWindow? Window, RefillRefusal? Refusal)> NextCollectableWindowAsync(
        Prescription rx, PrescriptionLine line, DateTimeOffset now, CancellationToken ct)
    {
        var windows = await db.DispenseWindows.AsNoTracking()
            .Where(w => w.PrescriptionLineId == line.PrescriptionLineId)
            .OrderBy(w => w.WindowNo).ToListAsync(ct);
        if (windows.Count == 0) return (null, null);   // acute, or a chronic line issued before 30.x

        // THE DEFECT THE 2026-08-09 AUDIT NAMED. A refill window opening today was compared against the UTC
        // date, which is still yesterday until 02:00 Cairo — so a patient at the counter on the morning their
        // medicine is due was told to come back, on the one day they were right to come.
        var today = BusinessCalendar.DateIn(now);
        Domain.ChronicDispenseDecision? firstRefusal = null;
        DateOnly? earliestOpen = null;

        foreach (var w in windows)
        {
            var decision = Domain.ChronicDispensing.Evaluate(
                new Prescribing.RefillWindow(
                    w.WindowNo, w.ScheduledOpenDate, w.OpensAt, w.ClosesAt,
                    w.AllocatedQuantity, w.DispensedQuantity,
                    Enum.TryParse<Prescribing.WindowStatus>(w.Status, out var st)
                        ? st : Prescribing.WindowStatus.Pending),
                today, eligibleNow: true, rx.ValidUntil);

            if (decision.Error == Domain.ChronicDispenseError.None) return (w, null);

            // Keep the FIRST refusal and the EARLIEST future opening: a beneficiary refused today needs the
            // next date they can come, not the reason the last window of the script is shut.
            firstRefusal ??= decision;
            if (decision.OpensAt is { } o && (earliestOpen is null || o < earliestOpen)) earliestOpen = o;
        }

        return (null, new RefillRefusal(
            firstRefusal?.Error.ToString() ?? "NoCollectableWindow", earliestOpen, 0m));
    }

    /// <summary>Canonical hash of what this dispense asks for — everything that changes the medication
    /// actually handed over, so a corrected quantity or a different batch cannot reuse the same key.</summary>
    private static string HashRequest(
        Guid prescriptionId, Guid lineId, decimal quantity, string batchNo, DateOnly expiryDate, Guid? substitutedDrugId) =>
        IdempotencyKeyRules.Hash(
            prescriptionId.ToString(), lineId.ToString(), IdempotencyKeyRules.Amount(quantity),
            batchNo, expiryDate.ToString("O"), substitutedDrugId?.ToString() ?? "-");

    /// <summary>Re-read the prescription's lines inside the transaction, recompute the aggregate status
    /// from them, and apply it as a compare-and-set. A racer that moved the Rx between our read and our
    /// write loses; we retry against the value it wrote. Returns the status the Rx actually holds.</summary>
    private async Task<RxStatus> ApplyAggregateStatusAsync(Guid prescriptionId, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (var attempt = 0; ; attempt++)
        {
            var fresh = await db.Prescriptions.AsNoTracking().Include(p => p.Lines)
                .FirstAsync(p => p.PrescriptionId == prescriptionId, ct);
            var current = fresh.Status;
            var recomputed = Domain.Dispensing.RecomputePrescriptionStatus(fresh);
            if (recomputed == current) return current;

            var affected = await db.Prescriptions
                .Where(p => p.PrescriptionId == prescriptionId && p.Status == current)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, recomputed), ct);
            if (affected == 1) return recomputed;

            if (attempt >= maxAttempts - 1)
                return await db.Prescriptions.AsNoTracking().Where(p => p.PrescriptionId == prescriptionId)
                    .Select(p => p.Status).FirstAsync(ct);
        }
    }

    private static DispenseOutcome Map(DispenseError error) => error switch
    {
        DispenseError.InvalidQuantity => DispenseOutcome.InvalidQuantity,
        DispenseError.LineNotFound => DispenseOutcome.LineNotFound,
        DispenseError.AlreadyDispensed => DispenseOutcome.AlreadyDispensed,
        DispenseError.OverDispense => DispenseOutcome.OverDispense,
        DispenseError.RxNotDispensable => DispenseOutcome.RxNotDispensable,
        DispenseError.ExpiredLot => DispenseOutcome.ExpiredLot,
        _ => DispenseOutcome.InvalidQuantity,
    };

    /// <summary>True when a save failed on a UNIQUE violation (Postgres SQLSTATE 23505) — the idempotency-key insert
    /// lost a race. Read via reflection to avoid a hard Npgsql compile dependency here.</summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex.InnerException; e is not null; e = e.InnerException)
            if (e.GetType().GetProperty("SqlState")?.GetValue(e) as string == "23505")
                return true;
        return false;
    }
}
