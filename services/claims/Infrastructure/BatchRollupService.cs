using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Infrastructure;

/// <summary>
/// Phase 18.A2 (audit R2 X2) — the ONE place a batch's rollup totals are computed.
///
/// Before this class existed there were three recompute paths: <c>AdjustmentService</c> summed the
/// applied <c>claim_adjustment.amount_delta</c> rows, while <c>DecisionService</c> and
/// <c>BatchService</c> called <c>BatchRollup.Compute(lines)</c> with <c>adjusted = 0</c>. Any decision
/// or batch transition after an adjustment therefore ERASED it — including the <c>→ Decided</c>
/// transition, which runs immediately before totals freeze at <c>SettlementIssued</c>. Deductions and
/// recoveries silently vanished from the settled <c>net_payable</c>.
///
/// The totals follow 36 §8 exactly: <c>claimed → priced → approved → adjustments → net payable</c>.
/// <c>approved</c> is the sum of DECIDED allowed amounts and <c>adjusted</c> is the sum of non-pending
/// signed deltas; they are disjoint, so the recompute is idempotent no matter how often it runs. A
/// frozen batch is never recomputed.
/// </summary>
public sealed class BatchRollupService(ClaimsDbContext db)
{
    /// <summary>Recompute from the batch's own active items. Used by every batch mutation path.</summary>
    public Task RecomputeAsync(ClaimBatch batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return RecomputeAsync(batch, ActiveClaimIds(batch), ct);
    }

    /// <summary>Recompute over an explicit claim set — for batch CREATE, where the items are not yet
    /// persisted and the batch's own collection is not yet authoritative.</summary>
    public async Task RecomputeAsync(ClaimBatch batch, IReadOnlyCollection<Guid> claimIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(claimIds);
        if (batch.FrozenAt is not null) return;   // frozen at SettlementIssued — totals are immutable (23 §9)

        // TRACKED read: a decision in flight has already mutated its line in the change tracker and has
        // not saved yet. AsNoTracking here would roll the batch back to the pre-decision figures.
        var claims = await db.Claims.Include(c => c.Lines).Where(c => claimIds.Contains(c.ClaimId)).ToListAsync(ct);
        var adjusted = await AppliedAdjustedAsync(claimIds, ct);

        var roll = BatchRollup.Compute(claims.SelectMany(c => c.Lines), adjusted);
        batch.TotalClaimed = roll.Claimed;
        batch.TotalPriced = roll.Priced;
        batch.TotalApproved = roll.Approved;
        batch.TotalAdjusted = roll.Adjusted;
        batch.TotalDenied = roll.Denied;
        batch.NetPayable = roll.NetPayable;
    }

    /// <summary>Recompute the batch a claim belongs to, if any. No-op for an unbatched claim.</summary>
    public async Task RecomputeForClaimAsync(Claim claim, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (claim.BatchId is not { } batchId) return;
        var batch = await db.ClaimBatches.Include(b => b.Items).FirstOrDefaultAsync(b => b.BatchId == batchId, ct);
        if (batch is null) return;
        await RecomputeAsync(batch, ct);
    }

    /// <summary>Recompute one claim's own totals — the same two disjoint components at claim scope.</summary>
    public async Task RecomputeClaimTotalsAsync(Claim claim, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var roll = BatchRollup.Compute(claim.Lines, await AppliedAdjustedAsync([claim.ClaimId], ct));
        claim.ApprovedAmount = roll.Approved;
        claim.AdjustedAmount = roll.Adjusted;
        claim.NetPayable = roll.NetPayable;
    }

    /// <summary>Σ of signed deltas from adjustments that have TAKEN EFFECT. A dual-control adjustment
    /// awaiting its second approver stays <c>PendingSecondApproval</c> and is excluded; the confirming
    /// row a second approver adds carries the delta and IS counted (36 §7).</summary>
    public async Task<decimal> AppliedAdjustedAsync(IReadOnlyCollection<Guid> claimIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claimIds);
        if (claimIds.Count == 0) return 0m;

        // Local (unsaved) rows count too: an adjustment being raised in this transaction must be part of
        // the totals it triggers, otherwise the batch is briefly wrong until the next recompute.
        var persisted = await db.ClaimAdjustments.AsNoTracking()
            .Where(a => claimIds.Contains(a.ClaimId) && !a.PendingSecondApproval)
            .SumAsync(a => (decimal?)a.AmountDelta, ct) ?? 0m;

        var local = db.ChangeTracker.Entries<ClaimAdjustment>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .Where(a => claimIds.Contains(a.ClaimId) && !a.PendingSecondApproval)
            .Sum(a => a.AmountDelta);

        return persisted + local;
    }

    private static IReadOnlyCollection<Guid> ActiveClaimIds(ClaimBatch batch) =>
        batch.Items.Where(i => i.RemovedAt is null).Select(i => i.ClaimId).ToList();
}
