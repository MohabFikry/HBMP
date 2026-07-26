using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;

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
public sealed class AdjustmentService(ClaimsDbContext db, TimeProvider clock)
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

        var before = CurrentPayable(line);
        var after = before + req.AmountDelta;

        // Dual control: if the scope (batch if batched, else claim) net payable would go negative, a second approver
        // must confirm before the adjustment takes effect. The row is recorded PENDING and the line is NOT changed.
        var wouldGoNegative = await ScopeNetWithDeltaAsync(claim, req.AmountDelta, ct) < 0m;
        var row = NewRow(tenantId, claim, line, req, actor, correlationId, idempotencyKey, before, after, pending: wouldGoNegative, confirms: null);
        db.ClaimAdjustments.Add(row);

        if (wouldGoNegative)
            return await SaveAsync(AdjustmentOutcome.PendingSecondApproval, row, claim, line, ct);

        ApplyEffect(claim, line, req.Type, after);
        await RecomputeBatchAsync(claim, ct);
        return await SaveAsync(AdjustmentOutcome.Recorded, row, claim, line, ct);
    }

    private async Task<AdjustmentResult> ConfirmAsync(
        string tenantId, string actor, Claim claim, ClaimLine line, Guid confirmId,
        string? idempotencyKey, string correlationId, CancellationToken ct)
    {
        var pending = await db.ClaimAdjustments.FirstOrDefaultAsync(
            a => a.AdjustmentId == confirmId && a.ClaimLineId == line.ClaimLineId && a.PendingSecondApproval, ct);
        if (pending is null) return Fail(AdjustmentOutcome.DualControlNotPending);
        if (string.Equals(pending.AdjustedBy, actor, StringComparison.Ordinal)) return Fail(AdjustmentOutcome.SoDSameApprover);

        var req = new AdjustmentRequest(pending.AdjustmentType, pending.AmountDelta, pending.ReasonCode, pending.Rationale, pending.RecoversClaimLineId, null);
        var confirming = NewRow(tenantId, claim, line, req, actor, correlationId, idempotencyKey,
            pending.BeforeAmount, pending.AfterAmount, pending: false, confirms: pending.AdjustmentId);
        db.ClaimAdjustments.Add(confirming);

        ApplyEffect(claim, line, pending.AdjustmentType, pending.AfterAmount);
        await RecomputeBatchAsync(claim, ct);
        return await SaveAsync(AdjustmentOutcome.Confirmed, confirming, claim, line, ct);
    }

    // ---- effect + rollup ----------------------------------------------------------------------------------
    private static decimal CurrentPayable(ClaimLine line) =>
        line.AllowedAmount ?? line.ContractPrice ?? line.BilledAmount;

    private static void ApplyEffect(Claim claim, ClaimLine line, AdjustmentType type, decimal after)
    {
        line.Status = AdjustmentRules.ResultingStatus(type);
        if (line.Status == ClaimLineStatus.Adjusted) line.AllowedAmount = Math.Max(0m, after);
        // recompute the claim's own rollup so its net payable stays consistent line-by-line.
        var roll = BatchRollup.Compute(claim.Lines);
        claim.ApprovedAmount = roll.Approved;
        claim.AdjustedAmount = (claim.AdjustedAmount ?? 0m);
    }

    private async Task<decimal> ScopeNetWithDeltaAsync(Claim claim, decimal newDelta, CancellationToken ct)
    {
        // Net payable = approved(lines) + adjusted(applied adjustment deltas) + newDelta, over the batch if batched,
        // else this claim. Pending adjustments do not count until confirmed.
        List<Guid> claimIds;
        if (claim.BatchId is { } batchId)
        {
            var batch = await db.ClaimBatches.Include(b => b.Items).FirstOrDefaultAsync(b => b.BatchId == batchId, ct);
            claimIds = batch is null ? [claim.ClaimId] : batch.Items.Where(i => i.RemovedAt is null).Select(i => i.ClaimId).ToList();
        }
        else claimIds = [claim.ClaimId];

        var lines = await db.ClaimLines.AsNoTracking().Where(l => claimIds.Contains(l.ClaimId)).ToListAsync(ct);
        var approved = BatchRollup.Compute(lines).Approved;
        var appliedAdjusted = await db.ClaimAdjustments.AsNoTracking()
            .Where(a => claimIds.Contains(a.ClaimId) && !a.PendingSecondApproval)
            .SumAsync(a => (decimal?)a.AmountDelta, ct) ?? 0m;
        return approved + appliedAdjusted + newDelta;
    }

    private async Task RecomputeBatchAsync(Claim claim, CancellationToken ct)
    {
        if (claim.BatchId is not { } batchId) return;
        var batch = await db.ClaimBatches.Include(b => b.Items).FirstOrDefaultAsync(b => b.BatchId == batchId, ct);
        if (batch is null || batch.FrozenAt is not null) return;
        var claimIds = batch.Items.Where(i => i.RemovedAt is null).Select(i => i.ClaimId).ToList();
        var lines = await db.ClaimLines.AsNoTracking().Where(l => claimIds.Contains(l.ClaimId)).ToListAsync(ct);
        var adjusted = await db.ClaimAdjustments.AsNoTracking()
            .Where(a => claimIds.Contains(a.ClaimId) && !a.PendingSecondApproval)
            .SumAsync(a => (decimal?)a.AmountDelta, ct) ?? 0m;
        var roll = BatchRollup.Compute(lines, adjusted);
        batch.TotalClaimed = roll.Claimed; batch.TotalPriced = roll.Priced; batch.TotalApproved = roll.Approved;
        batch.TotalAdjusted = roll.Adjusted; batch.TotalDenied = roll.Denied; batch.NetPayable = roll.NetPayable;
    }

    private async Task<AdjustmentResult> SaveAsync(
        AdjustmentOutcome outcome, ClaimAdjustment adjustment, Claim claim, ClaimLine line, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new AdjustmentResult(outcome, adjustment, claim, line);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
            return Fail(AdjustmentOutcome.Conflict);
        }
        catch (DbUpdateException ex) when (IsUnique(ex, "ux_adjustment_idempotency"))
        {
            await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
            var prior = await db.ClaimAdjustments.AsNoTracking().FirstAsync(a => a.IdempotencyKey == adjustment.IdempotencyKey, ct);
            return new AdjustmentResult(AdjustmentOutcome.Replayed, prior, claim, line);
        }
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
