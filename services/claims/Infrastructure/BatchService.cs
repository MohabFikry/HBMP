using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Infrastructure;

/// <summary>Outcome of a batch operation, mapped to problem responses at the edge.</summary>
public enum BatchOutcome
{
    Ok, NotFound, IllegalTransition, AlreadyBatched, MembershipLocked, EmptyBatch,
    UndecidedLines, ProviderMismatch, ReasonRequired, PayeeRequired,
}

public sealed record BatchResult(BatchOutcome Outcome, ClaimBatch? Batch, IReadOnlyList<Guid> UndecidedClaimLines)
{
    public static BatchResult Fail(BatchOutcome o) => new(o, null, []);
    public static BatchResult Ok(ClaimBatch b) => new(BatchOutcome.Ok, b, []);
}

/// <summary>A batch selector for creation. Only the fields a given mode needs are used; the rest are ignored.</summary>
public sealed record BatchSelector(
    BatchType BatchType, BatchSelectionMode SelectionMode,
    Guid? PayeeProviderId, Guid? ProviderLocationId, Guid? ProviderGroupId,
    DateOnly PeriodFrom, DateOnly PeriodTo,
    DateOnly? ServiceDateFrom, DateOnly? ServiceDateTo, IReadOnlyList<Guid>? ClaimIds);

/// <summary>Batching (10b.2). Creates a batch from a selector, adds/removes claims (recorded, never deleted), and
/// drives the 23 §9 lifecycle with its guards. The single-open-batch guarantee is the DB index
/// <c>ux_claim_one_open_batch</c> (a claim can never sit in two live batches); this class keeps the item's
/// materialized batch_status in step on every transition and recomputes rollups (frozen at SettlementIssued).</summary>
public sealed class BatchService(ClaimsDbContext db, BatchNoIssuer batchNo, TimeProvider clock)
{
    // ---- create -------------------------------------------------------------------------------------------
    public async Task<BatchResult> CreateAsync(string tenantId, string? actor, BatchSelector sel, CancellationToken ct)
    {
        if (sel.BatchType == BatchType.Provider && sel.SelectionMode != BatchSelectionMode.Manual
            && sel.PayeeProviderId is null && sel.ProviderGroupId is null)
            return BatchResult.Fail(BatchOutcome.PayeeRequired);

        var candidates = await SelectCandidatesAsync(tenantId, sel, ct);
        // Manual/Provider batches must be provider-homogeneous (one payee).
        var payee = sel.PayeeProviderId ?? sel.ProviderGroupId;
        if (sel.BatchType == BatchType.Provider)
        {
            var providers = candidates.Select(c => c.ProviderId).Distinct().ToList();
            if (payee is null && providers.Count == 1) payee = providers[0];
            if (payee is null) return BatchResult.Fail(BatchOutcome.PayeeRequired);
            if (providers.Any(p => p != payee)) return BatchResult.Fail(BatchOutcome.ProviderMismatch);
        }

        var batch = new ClaimBatch
        {
            BatchId = Guid.NewGuid(),
            BatchNo = await batchNo.NextAsync(sel.PeriodFrom.Year, ct),
            BatchType = sel.BatchType,
            SelectionMode = sel.SelectionMode,
            PayeeProviderId = sel.BatchType == BatchType.Provider ? payee : null,
            ProviderLocationId = sel.ProviderLocationId,
            TenantId = tenantId,
            PeriodFrom = sel.PeriodFrom,
            PeriodTo = sel.PeriodTo,
            Status = BatchStatus.Open,
            CreatedBy = actor,
            CreatedAt = clock.GetUtcNow(),
        };
        db.ClaimBatches.Add(batch);
        foreach (var c in candidates)
        {
            db.ClaimBatchItems.Add(NewItem(batch.BatchId, c.ClaimId, actor));
            c.BatchId = batch.BatchId;
        }
        Recompute(batch, candidates);

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUnique(ex, "ux_claim_one_open_batch"))
        {
            db.ChangeTracker.Clear();
            return BatchResult.Fail(BatchOutcome.AlreadyBatched); // a candidate was concurrently batched elsewhere
        }
        return BatchResult.Ok(batch);
    }

    // ---- membership ---------------------------------------------------------------------------------------
    public async Task<BatchResult> AddClaimAsync(string tenantId, string? actor, Guid batchId, Guid claimId, CancellationToken ct)
    {
        var batch = await LoadAsync(tenantId, batchId, ct);
        if (batch is null) return BatchResult.Fail(BatchOutcome.NotFound);
        if (BatchTransitions.IsMembershipLocked(batch.Status) || batch.Status == BatchStatus.Cancelled)
            return BatchResult.Fail(BatchOutcome.MembershipLocked);

        var claim = await db.Claims.Include(c => c.Lines).FirstOrDefaultAsync(c => c.ClaimId == claimId && c.TenantId == tenantId, ct);
        if (claim is null) return BatchResult.Fail(BatchOutcome.NotFound);
        if (batch.BatchType == BatchType.Provider && claim.ProviderId != batch.PayeeProviderId)
            return BatchResult.Fail(BatchOutcome.ProviderMismatch);

        db.ClaimBatchItems.Add(NewItem(batch.BatchId, claimId, actor, batch.Status));
        claim.BatchId = batch.BatchId;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUnique(ex, "ux_claim_one_open_batch"))
        {
            db.ChangeTracker.Clear();
            return BatchResult.Fail(BatchOutcome.AlreadyBatched);
        }
        await RecomputeFromDbAsync(batch, ct);
        return BatchResult.Ok(batch);
    }

    public async Task<BatchResult> RemoveClaimAsync(string tenantId, string? actor, Guid batchId, Guid claimId, string? reason, CancellationToken ct)
    {
        var batch = await LoadAsync(tenantId, batchId, ct);
        if (batch is null) return BatchResult.Fail(BatchOutcome.NotFound);
        if (BatchTransitions.IsMembershipLocked(batch.Status) || batch.Status == BatchStatus.Cancelled)
            return BatchResult.Fail(BatchOutcome.MembershipLocked);
        // Removal from an UnderReview batch is an audited exception and REQUIRES a reason.
        if (batch.Status == BatchStatus.UnderReview && string.IsNullOrWhiteSpace(reason))
            return BatchResult.Fail(BatchOutcome.ReasonRequired);

        var item = batch.Items.FirstOrDefault(i => i.ClaimId == claimId && i.RemovedAt is null);
        if (item is null) return BatchResult.Fail(BatchOutcome.NotFound);
        item.RemovedAt = clock.GetUtcNow();
        item.RemovedBy = actor;
        item.RemovalReason = reason;

        var claim = await db.Claims.FirstOrDefaultAsync(c => c.ClaimId == claimId && c.TenantId == tenantId, ct);
        if (claim is not null) claim.BatchId = null;

        await db.SaveChangesAsync(ct);
        await RecomputeFromDbAsync(batch, ct);
        return BatchResult.Ok(batch);
    }

    // ---- lifecycle ----------------------------------------------------------------------------------------
    public async Task<BatchResult> TransitionAsync(string tenantId, Guid batchId, BatchStatus to, string? reason, CancellationToken ct)
    {
        var batch = await LoadAsync(tenantId, batchId, ct);
        if (batch is null) return BatchResult.Fail(BatchOutcome.NotFound);
        if (!BatchTransitions.CanTransition(batch.Status, to)) return BatchResult.Fail(BatchOutcome.IllegalTransition);

        var activeItems = batch.Items.Where(i => i.RemovedAt is null).ToList();
        if (to == BatchStatus.UnderReview && activeItems.Count == 0) return BatchResult.Fail(BatchOutcome.EmptyBatch);
        if (to is BatchStatus.Cancelled && batch.Status == BatchStatus.UnderReview && string.IsNullOrWhiteSpace(reason))
            return BatchResult.Fail(BatchOutcome.ReasonRequired);

        if (to == BatchStatus.Decided)
        {
            // Decided requires EVERY line of every member claim to carry a recorded decision (non-Pending).
            var claimIds = activeItems.Select(i => i.ClaimId).ToList();
            var undecided = await db.ClaimLines.AsNoTracking()
                .Where(l => claimIds.Contains(l.ClaimId) && l.Status == ClaimLineStatus.Pending)
                .Select(l => l.ClaimLineId).ToListAsync(ct);
            if (undecided.Count > 0) return new BatchResult(BatchOutcome.UndecidedLines, batch, undecided);
            batch.DecidedAt = clock.GetUtcNow();
        }

        batch.Status = to;
        // Keep the item snapshot in step so the single-open-batch index reflects the new status (a Decided/Cancelled
        // batch frees its claims from the "open batch" predicate).
        foreach (var i in activeItems) i.BatchStatusSnapshot = to;

        if (to == BatchStatus.SettlementIssued) batch.FrozenAt = clock.GetUtcNow(); // rollups are frozen here
        else await RecomputeFromDbAsync(batch, ct);

        await db.SaveChangesAsync(ct);
        return BatchResult.Ok(batch);
    }

    // ---- helpers ------------------------------------------------------------------------------------------
    private async Task<ClaimBatch?> LoadAsync(string tenantId, Guid batchId, CancellationToken ct) =>
        await db.ClaimBatches.Include(b => b.Items)
            .FirstOrDefaultAsync(b => b.BatchId == batchId && b.TenantId == tenantId, ct);

    private ClaimBatchItem NewItem(Guid batchId, Guid claimId, string? actor, BatchStatus status = BatchStatus.Open) => new()
    {
        BatchItemId = Guid.NewGuid(), BatchId = batchId, ClaimId = claimId,
        AddedBy = actor, AddedAt = clock.GetUtcNow(), BatchStatusSnapshot = status,
    };

    private async Task<List<Claim>> SelectCandidatesAsync(string tenantId, BatchSelector sel, CancellationToken ct)
    {
        var q = db.Claims.Include(c => c.Lines).Where(c => c.TenantId == tenantId);
        q = sel.BatchType == BatchType.Reimbursement
            ? q.Where(c => c.Origin == ClaimOrigin.Reimbursement)
            : q.Where(c => c.Origin != ClaimOrigin.Reimbursement);

        switch (sel.SelectionMode)
        {
            case BatchSelectionMode.Manual:
                var ids = sel.ClaimIds ?? [];
                q = q.Where(c => ids.Contains(c.ClaimId));
                break;
            case BatchSelectionMode.ProviderBranch:
                q = q.Where(c => c.ProviderLocationId == sel.ProviderLocationId);
                goto case BatchSelectionMode.DateRange;
            case BatchSelectionMode.ProviderGroup:
                // Chain expansion is deferred to provider-service; locally the group id is the payee provider.
                if (sel.ProviderGroupId is { } g) q = q.Where(c => c.ProviderId == g);
                goto case BatchSelectionMode.DateRange;
            case BatchSelectionMode.DateRange:
                if (sel.PayeeProviderId is { } p && sel.BatchType == BatchType.Provider) q = q.Where(c => c.ProviderId == p);
                var from = sel.ServiceDateFrom ?? sel.PeriodFrom;
                var to = sel.ServiceDateTo ?? sel.PeriodTo;
                q = q.Where(c => c.ServiceDateFrom >= from && c.ServiceDateFrom <= to);
                break;
        }

        var rows = await q.ToListAsync(ct);
        // Exclude claims already sitting in a live (Open/UnderReview) batch — the DB index is the final guard, this
        // just keeps a fresh create clean.
        var live = await db.ClaimBatchItems.AsNoTracking()
            .Where(i => i.RemovedAt == null &&
                        (i.BatchStatusSnapshot == BatchStatus.Open || i.BatchStatusSnapshot == BatchStatus.UnderReview))
            .Select(i => i.ClaimId).ToListAsync(ct);
        var liveSet = live.ToHashSet();
        return rows.Where(c => !liveSet.Contains(c.ClaimId)).ToList();
    }

    private async Task RecomputeFromDbAsync(ClaimBatch batch, CancellationToken ct)
    {
        var claimIds = batch.Items.Where(i => i.RemovedAt is null).Select(i => i.ClaimId).ToList();
        var claims = await db.Claims.Include(c => c.Lines).Where(c => claimIds.Contains(c.ClaimId)).ToListAsync(ct);
        Recompute(batch, claims);
    }

    private static void Recompute(ClaimBatch batch, IReadOnlyCollection<Claim> claims)
    {
        var roll = BatchRollup.Compute(claims.SelectMany(c => c.Lines));
        batch.TotalClaimed = roll.Claimed;
        batch.TotalPriced = roll.Priced;
        batch.TotalApproved = roll.Approved;
        batch.TotalAdjusted = roll.Adjusted;
        batch.TotalDenied = roll.Denied;
        batch.NetPayable = roll.NetPayable;
    }

    private static bool IsUnique(DbUpdateException ex, string constraint)
    {
        for (Exception? e = ex.InnerException; e is not null; e = e.InnerException)
            if (e.GetType().GetProperty("SqlState")?.GetValue(e) as string == "23505")
                return (e.GetType().GetProperty("ConstraintName")?.GetValue(e) as string) == constraint;
        return false;
    }
}
