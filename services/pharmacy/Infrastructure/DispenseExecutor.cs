using Mersal.Pharmacy.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Infrastructure;

/// <summary>The outcome of an atomic dispense attempt. <c>Applied</c> and <c>Replayed</c> succeed; the rest map to
/// problem responses at the edge. <c>Conflict</c> means another racer won the line's version — re-read and retry.</summary>
public enum DispenseOutcome
{
    Applied, Replayed, Conflict, NotFound, AlreadyDispensed, OverDispense, RxNotDispensable, LineNotFound, InvalidQuantity, ExpiredLot,
}

public sealed record DispenseResult(DispenseOutcome Outcome, Prescription? Prescription, DispenseEvent? Event)
{
    public static DispenseResult Fail(DispenseOutcome outcome) => new(outcome, null, null);
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
        DateTimeOffset now,
        Func<Prescription, DispenseEvent, CancellationToken, Task>? insideTransaction = null,
        CancellationToken ct = default)
    {
        var rx = await db.Prescriptions.Include(p => p.Lines).FirstOrDefaultAsync(p => p.PrescriptionId == prescriptionId, ct);
        if (rx is null) return DispenseResult.Fail(DispenseOutcome.NotFound);

        // (3) Idempotent replay: this key already produced a dispense_event → return it unchanged.
        var prior = await db.DispenseEvents.AsNoTracking().FirstOrDefaultAsync(d => d.IdempotencyKey == idempotencyKey, ct);
        if (prior is not null) return new DispenseResult(DispenseOutcome.Replayed, rx, prior);

        var error = Domain.Dispensing.Validate(rx, lineId, quantity, expiryDate, now);
        if (error != DispenseError.None) return DispenseResult.Fail(Map(error));

        var line = rx.Lines.First(l => l.PrescriptionLineId == lineId);
        line.QuantityDispensed += quantity;
        line.Status = Domain.Dispensing.RecomputeLineStatus(line);
        var evt = new DispenseEvent
        {
            DispenseId = Guid.NewGuid(), PrescriptionLineId = lineId, DispensingPharmacyId = dispensingPharmacyId,
            Quantity = quantity, IdempotencyKey = idempotencyKey, BatchNo = batchNo, ExpiryDate = expiryDate,
            SubstitutedDrugId = substitutedDrugId, SubstitutionReason = substitutionReason,
            DispensedAt = now, DispensedBy = actorId,
        };
        db.DispenseEvents.Add(evt);
        var newRxStatus = Domain.Dispensing.RecomputePrescriptionStatus(rx);

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
            var fresh = await db.Prescriptions.AsNoTracking().Include(p => p.Lines).FirstAsync(p => p.PrescriptionId == prescriptionId, ct);
            return new DispenseResult(DispenseOutcome.Replayed, fresh, winner);
        }

        if (newRxStatus != rx.Status)
        {
            // Prescription status is updated out of the line's optimistic guard so concurrent dispenses of DIFFERENT
            // lines never falsely collide on the prescription row.
            await db.Prescriptions.Where(p => p.PrescriptionId == prescriptionId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, newRxStatus), ct);
            rx.Status = newRxStatus;
        }

        if (insideTransaction is not null) await insideTransaction(rx, evt, ct);
        await tx.CommitAsync(ct);
        return new DispenseResult(DispenseOutcome.Applied, rx, evt);
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
