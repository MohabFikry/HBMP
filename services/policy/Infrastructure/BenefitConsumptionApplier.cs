using Mersal.Events;
using Mersal.Policy.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Infrastructure;

/// <summary>
/// Phase 18.A1 (audit R2 X1) — the ONLY writer of <c>coverage_limit.consumed_value</c>.
///
/// One fulfillment event ⇒ one transaction that (1) claims the instruction's <c>source_ref</c> in the
/// append-only <c>benefit_consumption</c> ledger, (2) moves every accumulating limit on the applicable
/// coverage with an ATOMIC guarded UPDATE, and (3) stages <c>CoverageLimitChanged</c> so the eligibility
/// projection invalidates. All three commit together, so the accumulator can never drift from the
/// fulfillment ledger.
///
/// The guard is the UPDATE's own WHERE clause (<c>consumed_value + delta &gt;= 0</c>) evaluated inside a
/// single statement, so N concurrent movers serialize at the row lock and each increment lands exactly
/// once — no read-modify-write window. A reversal larger than what was consumed matches zero rows and is
/// REFUSED (<see cref="ConsumptionOutcome.WouldGoNegative"/>) rather than clamped to a false zero.
///
/// Every no-move outcome is written to the ledger too: a skipped accumulation must be visible, never
/// silent. Claims never reach this class — the claims path reads the accumulator only (FR-CLM-057).
/// </summary>
public sealed class BenefitConsumptionApplier(PolicyDbContext db, IOutbox outbox, TimeProvider clock)
{
    public async Task<ConsumptionResult> ApplyAsync(ConsumptionInstruction instruction, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        if (instruction.Quantity < 0)
            throw new ArgumentOutOfRangeException(nameof(instruction), "quantity must be non-negative; use Direction.Reversed to decrement.");

        // Already applied under this source_ref? Cheap pre-check; the UNIQUE index is the real guarantee.
        if (await db.BenefitConsumptions.AsNoTracking().AnyAsync(r => r.SourceRef == instruction.SourceRef, ct))
            return ConsumptionResult.None(ConsumptionOutcome.Replayed);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var (outcome, coverageId, limitIds) = await ResolveAsync(instruction, ct);

        var record = new BenefitConsumptionRecord
        {
            ConsumptionId = Guid.NewGuid(),
            TenantId = instruction.TenantId,
            EventId = instruction.EventId,
            EventType = instruction.EventType,
            SourceRef = instruction.SourceRef,
            BeneficiaryId = instruction.BeneficiaryId,
            BenefitCategory = instruction.BenefitCategory,
            CoverageId = coverageId,
            Quantity = instruction.Quantity,
            Direction = instruction.Direction,
            Outcome = outcome,
            MovedLimits = 0,
            AppliedAt = clock.GetUtcNow(),
        };
        db.BenefitConsumptions.Add(record);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent delivery of the same event won the claim — this one is a pure no-op.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return ConsumptionResult.None(ConsumptionOutcome.Replayed);
        }

        if (outcome is not (ConsumptionOutcome.Applied or ConsumptionOutcome.Reversed))
        {
            await tx.CommitAsync(ct);   // the deliberate no-move is itself a durable, auditable fact
            db.ChangeTracker.Clear();
            return ConsumptionResult.None(outcome, coverageId);
        }

        var delta = BenefitAccumulation.SignedDelta(instruction.Direction, instruction.Quantity);
        var moved = new List<Guid>();
        foreach (var limitId in limitIds)
        {
            // Atomic guarded move: one statement, no read-modify-write window, cannot go negative.
            var affected = await db.CoverageLimits
                .Where(l => l.CoverageLimitId == limitId && l.ConsumedValue + delta >= 0m)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ConsumedValue, l => l.ConsumedValue + delta), ct);

            if (affected == 0)
            {
                // Only reachable on a reversal that exceeds what was consumed — refuse the whole move.
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return ConsumptionResult.None(ConsumptionOutcome.WouldGoNegative, coverageId);
            }
            moved.Add(limitId);
        }

        await db.BenefitConsumptions.Where(r => r.ConsumptionId == record.ConsumptionId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.MovedLimits, moved.Count), ct);

        // Invalidate the eligibility projection with the POST-move remaining (the consumer already exists).
        var fresh = await db.CoverageLimits.AsNoTracking()
            .Where(l => moved.Contains(l.CoverageLimitId)).ToListAsync(ct);
        foreach (var l in fresh)
        {
            await outbox.EnqueueAsync("CoverageLimitChanged", "policy.events", new
            {
                coverageLimitId = l.CoverageLimitId,
                coverageId = l.CoverageId,
                tenantId = instruction.TenantId,
                limitType = l.LimitType.ToString(),
                l.LimitValue,
                l.ConsumedValue,
                remaining = l.Remaining,
                reason = instruction.Direction == ConsumptionDirection.Reversed ? "fulfillment-reversed" : "fulfillment-consumed",
            }, ct);
        }

        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new ConsumptionResult(outcome, coverageId, moved);
    }

    /// <summary>Resolve the applicable coverage and its accumulating limits, or the reason there is none.</summary>
    private async Task<(ConsumptionOutcome Outcome, Guid? CoverageId, IReadOnlyList<Guid> LimitIds)> ResolveAsync(
        ConsumptionInstruction instruction, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(instruction.BenefitCategory))
            return (ConsumptionOutcome.NoBenefitCategory, null, []);

        var categoryId = await db.BenefitCategories.AsNoTracking()
            .Where(c => c.Code == instruction.BenefitCategory)
            .Select(c => (Guid?)c.BenefitCategoryId).FirstOrDefaultAsync(ct);
        if (categoryId is null) return (ConsumptionOutcome.NoBenefitCategory, null, []);

        var candidates = await db.Coverages.AsNoTracking().Include(c => c.Limits)
            .Where(c => c.BeneficiaryId == instruction.BeneficiaryId && c.BenefitCategoryId == categoryId)
            .ToListAsync(ct);

        var coverage = candidates.FirstOrDefault(c =>
            BenefitAccumulation.IsApplicable(c.Status, c.IsDeleted, c.EffectiveFrom, c.EffectiveTo, instruction.OnDate));
        if (coverage is null) return (ConsumptionOutcome.NoCoverage, null, []);

        var limitIds = coverage.Limits
            .Where(l => BenefitAccumulation.Accumulates(l.LimitType))
            .Select(l => l.CoverageLimitId).ToList();

        var outcome = instruction.Direction == ConsumptionDirection.Reversed
            ? ConsumptionOutcome.Reversed
            : ConsumptionOutcome.Applied;

        return limitIds.Count == 0
            ? (ConsumptionOutcome.NoAccumulatingLimit, coverage.CoverageId, [])
            : (outcome, coverage.CoverageId, limitIds);
    }

    /// <summary>True when a save failed on a UNIQUE violation (Postgres SQLSTATE 23505) — the source_ref
    /// claim lost a race. Read via reflection to avoid a hard Npgsql compile dependency here.</summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex.InnerException; e is not null; e = e.InnerException)
            if (e.GetType().GetProperty("SqlState")?.GetValue(e) as string == "23505")
                return true;
        return false;
    }
}
