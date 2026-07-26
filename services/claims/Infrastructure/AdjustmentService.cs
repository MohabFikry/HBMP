using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mersal.Claims.Infrastructure;

public enum AdjustmentOutcome
{
    Recorded, PendingSecondApproval, Confirmed, Replayed, NotFound,
    SoDSameApprover, DualControlNotPending, Conflict, Validation,
}

public sealed record AdjustmentRequest(
    AdjustmentType Type, decimal AmountDelta, string? ReasonCode, string? Rationale,
    Guid? RecoversClaimLineId, Guid? ConfirmsAdjustmentId);

public sealed record AdjustmentResult(
    AdjustmentOutcome Outcome, ClaimAdjustment? Adjustment, Claim? Claim, ClaimLine? Line, string? ValidationError = null);

/// <summary>Append-only line adjustments (10b.7, 36 §7). Every adjustment is a NEW signed <c>claim_adjustment</c> row —
/// the original decision is never mutated or deleted. Enforced HERE: mandatory reason + rationale, sign-per-type, a
/// Recovery/Clawback references the original line, and DUAL CONTROL when the batch (or claim) net payable would go
/// NEGATIVE — the adjustment is recorded PENDING and takes effect only when a second, distinct approver confirms. Each
/// row carries the BEFORE/AFTER payable amounts; adjustments net into the batch rollup. Reversal/Void voids the line
/// (a compensating entry), every other type marks it Adjusted.</summary>
public sealed class AdjustmentService(ClaimsDbContext db, BatchRollupService rollups, TimeProvider clock)
{
    public async Task<AdjustmentResult> RaiseAsync(
        string tenantId, string actor, Guid claimId, Guid lineId, AdjustmentRequest req,
        string? idempotencyKey, string correlationId, CancellationToken ct = default)
    {
        var claim = await db.Claims.Include(c => c.Lines).FirstOrDefaultAsync(c => c.ClaimId == claimId && c.TenantId == tenantId, ct);
        var line = claim?.Lines.FirstOrDefault(l => l.ClaimLineId == lineId);
        if (claim is null || line is null) return Fail(AdjustmentOutcome.NotFound);

        if (idempotencyKey is not null)
        {
            var prior = await db.ClaimAdjustments.AsNoTracking().FirstOrDefaultAsync(a => a.IdempotencyKey == idempotencyKey, ct);
            if (prior is not null) return new AdjustmentResult(AdjustmentOutcome.Replayed, prior, claim, line);
        }

        if (req.ConfirmsAdjustmentId is { } confirmId)
            return await ConfirmAsync(tenantId, actor, claim, line, confirmId, idempotencyKey, correlationId, ct);

        var err = AdjustmentRules.Validate(req.Type, req.AmountDelta, req.ReasonCode, req.Rationale, req.RecoversClaimLineId);
        if (err is not null) return new AdjustmentResult(AdjustmentOutcome.Validation, null, claim, line, err);

        // 18.A4 — TOCTOU: the dual-control net used to be computed from an AsNoTracking read and acted on
        // in a LATER transaction. Two adjustments racing each other both saw a positive net, both were
        // recorded as applied, and the batch went negative with NO second approver anywhere. The scope is
        // now locked for the duration of the decision.
        await using var gate = await db.Database.BeginTransactionAsync(ct);
        await LockScopeAsync(claim, ct);

        var before = CurrentPayable(line);
        var after = before + req.AmountDelta;

        // 18.A2: the line's payable is no longer clamped to 0 while the ledger row records the true
        // negative — the two used to disagree about the same money. The line's allowed_amount is now
        // left at the decided figure and the signed delta carries the movement, so BeforeAmount /
        // AfterAmount below are the single truthful record. A negative NET is still not silently
        // permitted: it routes to dual control, which is the spec's sanctioned path (36 §7).

        // Dual control: if the scope (batch if batched, else claim) net payable would go negative, a second approver
        // must confirm before the adjustment takes effect. The row is recorded PENDING and the line is NOT changed.
        var wouldGoNegative = await ScopeNetWithDeltaAsync(claim, req.AmountDelta, ct) < 0m;
        var row = NewRow(tenantId, claim, line, req, actor, correlationId, idempotencyKey, before, after, pending: wouldGoNegative, confirms: null);
        db.ClaimAdjustments.Add(row);

        if (wouldGoNegative)
        {
            var pendingResult = await SaveAsync(AdjustmentOutcome.PendingSecondApproval, row, claim, line, ct);
            await gate.CommitAsync(ct);
            return pendingResult;
        }

        ApplyEffect(line, req.Type);
        await rollups.RecomputeClaimTotalsAsync(claim, ct);
        await rollups.RecomputeForClaimAsync(claim, ct);
        var result = await SaveAsync(AdjustmentOutcome.Recorded, row, claim, line, ct);
        await gate.CommitAsync(ct);
        return result;
    }

    private async Task<AdjustmentResult> ConfirmAsync(
        string tenantId, string actor, Claim claim, ClaimLine line, Guid confirmId,
        string? idempotencyKey, string correlationId, CancellationToken ct)
    {
        // 18.A4: same scope lock as the raise path — the confirming write must serialize against any
        // other adjustment on this batch.
        await using var gate = await db.Database.BeginTransactionAsync(ct);
        await LockScopeAsync(claim, ct);

        var pending = await db.ClaimAdjustments.FirstOrDefaultAsync(
            a => a.AdjustmentId == confirmId && a.ClaimLineId == line.ClaimLineId && a.PendingSecondApproval, ct);
        if (pending is null) return Fail(AdjustmentOutcome.DualControlNotPending);
        if (string.Equals(pending.AdjustedBy, actor, StringComparison.Ordinal)) return Fail(AdjustmentOutcome.SoDSameApprover);

        var req = new AdjustmentRequest(pending.AdjustmentType, pending.AmountDelta, pending.ReasonCode, pending.Rationale, pending.RecoversClaimLineId, null);
        var confirming = NewRow(tenantId, claim, line, req, actor, correlationId, idempotencyKey,
            pending.BeforeAmount, pending.AfterAmount, pending: false, confirms: pending.AdjustmentId);
        db.ClaimAdjustments.Add(confirming);

        ApplyEffect(line, pending.AdjustmentType);
        await rollups.RecomputeClaimTotalsAsync(claim, ct);
        await rollups.RecomputeForClaimAsync(claim, ct);
        var confirmed = await SaveAsync(AdjustmentOutcome.Confirmed, confirming, claim, line, ct);
        if (confirmed.Outcome == AdjustmentOutcome.Confirmed) await gate.CommitAsync(ct);
        return confirmed;
    }

    // ---- effect + rollup ----------------------------------------------------------------------------------
    private static decimal CurrentPayable(ClaimLine line) =>
        line.AllowedAmount ?? line.ContractPrice ?? line.BilledAmount;

    /// <summary>18.A2 — the line's status moves, but its <c>allowed_amount</c> does NOT. The decided
    /// amount is what the officer approved; the adjustment's signed delta carries the change and is
    /// summed separately into <c>total_adjusted</c>. Writing the delta into <c>allowed_amount</c> as
    /// well double-counted it (36 §8: claimed → priced → approved → adjustments → net payable), and made
    /// the rollup depend on how many times it had run. The true before/after payable is recorded on the
    /// adjustment row itself.</summary>
    private static void ApplyEffect(ClaimLine line, AdjustmentType type) =>
        line.Status = AdjustmentRules.ResultingStatus(type);

    /// <summary>
    /// 18.A4 — take a row lock over the adjustment's scope so the dual-control net cannot move underneath
    /// the decision. The batch row is the natural serialization point when the claim is batched; an
    /// unbatched claim locks its own row. Blocking (not SKIP LOCKED) is correct here: a concurrent
    /// adjustment must WAIT and then see this one's effect, never bypass the threshold.
    /// </summary>
    private async Task LockScopeAsync(Claim claim, CancellationToken ct)
    {
        if (claim.BatchId is { } batchId)
            await db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM claims.claim_batch WHERE batch_id = {0} FOR UPDATE", [batchId], ct);
        else
            await db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM claims.claim WHERE claim_id = {0} FOR UPDATE", [claim.ClaimId], ct);
    }

    /// <summary>Net payable over the adjustment's scope (its batch if batched, else the claim) WITH the
    /// proposed delta applied — the dual-control trigger. Uses the same disjoint approved + adjusted
    /// components as the canonical rollup, so the threshold and the totals can never disagree.</summary>
    private async Task<decimal> ScopeNetWithDeltaAsync(Claim claim, decimal newDelta, CancellationToken ct)
    {
        List<Guid> claimIds;
        if (claim.BatchId is { } batchId)
        {
            var batch = await db.ClaimBatches.Include(b => b.Items).FirstOrDefaultAsync(b => b.BatchId == batchId, ct);
            claimIds = batch is null ? [claim.ClaimId] : batch.Items.Where(i => i.RemovedAt is null).Select(i => i.ClaimId).ToList();
        }
        else claimIds = [claim.ClaimId];

        var lines = await db.ClaimLines.AsNoTracking().Where(l => claimIds.Contains(l.ClaimId)).ToListAsync(ct);
        var approved = BatchRollup.Compute(lines).Approved;
        return approved + await rollups.AppliedAdjustedAsync(claimIds, ct) + newDelta;
    }

    /// <summary>
    /// Persist within the AMBIENT transaction when the caller has one (18.A4 opens a scope lock before
    /// the dual-control decision, and the write must land inside it), otherwise open a local one. Nesting
    /// a second BeginTransaction inside the lock would throw.
    /// </summary>
    private async Task<AdjustmentResult> SaveAsync(
        AdjustmentOutcome outcome, ClaimAdjustment adjustment, Claim claim, ClaimLine line, CancellationToken ct)
    {
        var ambient = db.Database.CurrentTransaction;
        var tx = ambient is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return new AdjustmentResult(outcome, adjustment, claim, line);
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(tx, ambient, ct);
            return Fail(AdjustmentOutcome.Conflict);
        }
        catch (DbUpdateException ex) when (IsUnique(ex, "ux_adjustment_idempotency"))
        {
            await RollbackAsync(tx, ambient, ct);
            var prior = await db.ClaimAdjustments.AsNoTracking().FirstAsync(a => a.IdempotencyKey == adjustment.IdempotencyKey, ct);
            return new AdjustmentResult(AdjustmentOutcome.Replayed, prior, claim, line);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    private async Task RollbackAsync(IDbContextTransaction? owned, IDbContextTransaction? ambient, CancellationToken ct)
    {
        await (owned ?? ambient)!.RollbackAsync(ct);
        db.ChangeTracker.Clear();
    }

    private ClaimAdjustment NewRow(
        string tenantId, Claim claim, ClaimLine line, AdjustmentRequest req, string actor, string correlationId,
        string? idempotencyKey, decimal before, decimal after, bool pending, Guid? confirms) => new()
    {
        AdjustmentId = Guid.NewGuid(), ClaimLineId = line.ClaimLineId, ClaimId = claim.ClaimId, TenantId = tenantId,
        AdjustmentType = req.Type, AmountDelta = req.AmountDelta, ReasonCode = req.ReasonCode!, Rationale = req.Rationale!,
        RecoversClaimLineId = req.RecoversClaimLineId, BeforeAmount = before, AfterAmount = after, AdjustedBy = actor,
        AdjustedAt = clock.GetUtcNow(), CorrelationId = correlationId, PendingSecondApproval = pending,
        ConfirmsAdjustmentId = confirms, IdempotencyKey = idempotencyKey,
    };

    private static AdjustmentResult Fail(AdjustmentOutcome o) => new(o, null, null, null);

    private static bool IsUnique(DbUpdateException ex, string constraint)
    {
        for (Exception? e = ex.InnerException; e is not null; e = e.InnerException)
            if (e.GetType().GetProperty("SqlState")?.GetValue(e) as string == "23505")
                return (e.GetType().GetProperty("ConstraintName")?.GetValue(e) as string) == constraint;
        return false;
    }
}
