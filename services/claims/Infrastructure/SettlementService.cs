using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Infrastructure;

/// <summary>Stores a rendered settlement advice in document-service's WORM bucket (MinIO object-lock / retention) so it
/// cannot be altered or deleted, and returns the document reference. The DEFAULT records the reference only (the real
/// object-lock upload lands with document-service integration). Immutability is ALSO guaranteed on the claims side by
/// the append-only <c>settlement_advice</c> row + content hash.</summary>
public interface ISettlementDocumentStore
{
    Task<Guid> StoreAsync(string tenantId, Guid batchId, RenderedFile file, string contentHash, string? bearerToken, CancellationToken ct = default);
}

/// <summary>Default WORM store — assigns a document reference; the physical object-lock upload is document-service's job.</summary>
public sealed class NullWormStore : ISettlementDocumentStore
{
    public Task<Guid> StoreAsync(string tenantId, Guid batchId, RenderedFile file, string contentHash, string? bearerToken, CancellationToken ct = default) =>
        Task.FromResult(Guid.NewGuid());
}

public enum SettlementOutcome { Generated, Regenerated, BatchNotDecided, NotFound }
public sealed record SettlementResult(SettlementOutcome Outcome, SettlementAdvice? Advice, ClaimBatch? Batch);

public enum ExportOutcome { Ok, ProviderDenied, NotFound }
public sealed record ExportResult(ExportOutcome Outcome, RenderedFile? File, int RowCount);

public enum PaymentRefOutcome { Recorded, BatchNotSettled, NotFound }

/// <summary>Settlement advice + exports (10b.8, 36 §8). On a Decided batch, generates an IMMUTABLE settlement advice
/// (append-only <c>settlement_advice</c> row + content hash + WORM document), references it from the batch, FREEZES the
/// rollups, and moves the batch to SettlementIssued. Regeneration writes a NEW version referencing the superseded one —
/// it never overwrites. Exports (CSV/XLSX/PDF) carry ZERO clinical fields and are audited + provider-isolated. Recording
/// an external payment reference is a fact only. <b>THE PLATFORM NEVER MOVES MONEY — there is no payout path here.</b></summary>
public sealed class SettlementService(ClaimsDbContext db, ISettlementDocumentStore store, TimeProvider clock)
{
    public async Task<SettlementResult> GenerateAsync(
        string tenantId, Guid batchId, string actor, string? bearerToken, CancellationToken ct = default)
    {
        var batch = await db.ClaimBatches.FirstOrDefaultAsync(b => b.BatchId == batchId && b.TenantId == tenantId, ct);
        if (batch is null) return new SettlementResult(SettlementOutcome.NotFound, null, null);
        var regen = batch.Status == BatchStatus.SettlementIssued;
        if (batch.Status != BatchStatus.Decided && !regen)
            return new SettlementResult(SettlementOutcome.BatchNotDecided, null, batch);

        var projection = await BuildProjectionAsync(batch, actor, ct);
        var file = SettlementRenderer.Render(projection, "CSV");
        var hash = SettlementRenderer.ContentHash(projection);
        var documentId = await store.StoreAsync(tenantId, batchId, file, hash, bearerToken, ct);

        var priorVersion = await db.SettlementAdvices.AsNoTracking()
            .Where(a => a.BatchId == batchId).OrderByDescending(a => a.Version).FirstOrDefaultAsync(ct);

        var advice = new SettlementAdvice
        {
            AdviceId = Guid.NewGuid(), BatchId = batchId, TenantId = tenantId, BatchNo = batch.BatchNo,
            PayeeProviderId = batch.PayeeProviderId, ProviderLocationId = batch.ProviderLocationId,
            PeriodFrom = batch.PeriodFrom, PeriodTo = batch.PeriodTo,
            Version = (priorVersion?.Version ?? 0) + 1, SupersedesAdviceId = priorVersion?.AdviceId,
            DocumentId = documentId, ContentHash = hash,
            TotalClaimed = projection.TotalClaimed, TotalPriced = projection.TotalPriced,
            TotalApproved = projection.TotalApproved, TotalAdjusted = projection.TotalAdjusted,
            TotalDenied = projection.TotalDenied, NetPayable = projection.NetPayable,
            GeneratedBy = actor, GeneratedAt = clock.GetUtcNow(),
        };
        db.SettlementAdvices.Add(advice);

        batch.SettlementDocumentId = documentId;
        if (!regen)
        {
            batch.Status = BatchStatus.SettlementIssued;
            batch.FrozenAt = clock.GetUtcNow();
        }
        await db.SaveChangesAsync(ct);
        return new SettlementResult(regen ? SettlementOutcome.Regenerated : SettlementOutcome.Generated, advice, batch);
    }

    public async Task<ExportResult> ExportAsync(
        string tenantId, Guid batchId, string format, string? callerProviderId, string actor, CancellationToken ct = default)
    {
        var batch = await db.ClaimBatches.AsNoTracking().FirstOrDefaultAsync(b => b.BatchId == batchId && b.TenantId == tenantId, ct);
        if (batch is null) return new ExportResult(ExportOutcome.NotFound, null, 0);
        // Provider isolation: a provider exports only its own batch.
        if (callerProviderId is not null && batch.PayeeProviderId?.ToString() != callerProviderId)
            return new ExportResult(ExportOutcome.ProviderDenied, null, 0);

        var projection = await BuildProjectionAsync(batch, actor, ct);
        var file = SettlementRenderer.Render(projection, format);
        return new ExportResult(ExportOutcome.Ok, file, projection.Lines.Count);
    }

    public async Task<PaymentRefOutcome> RecordPaymentReferenceAsync(
        string tenantId, Guid batchId, string reference, DateOnly paymentDate, string actor, CancellationToken ct = default)
    {
        var batch = await db.ClaimBatches.FirstOrDefaultAsync(b => b.BatchId == batchId && b.TenantId == tenantId, ct);
        if (batch is null) return PaymentRefOutcome.NotFound;
        if (batch.Status != BatchStatus.SettlementIssued) return PaymentRefOutcome.BatchNotSettled;

        // RECORD ONLY — this initiates no payment. The platform has no payout endpoint or payment rail.
        db.SettlementPaymentReferences.Add(new SettlementPaymentReference
        {
            PaymentReferenceId = Guid.NewGuid(), BatchId = batchId, TenantId = tenantId, Reference = reference,
            PaymentDate = paymentDate, RecordedBy = actor, RecordedAt = clock.GetUtcNow(),
        });
        batch.Status = BatchStatus.Closed;
        await db.SaveChangesAsync(ct);
        return PaymentRefOutcome.Recorded;
    }

    private async Task<SettlementProjection> BuildProjectionAsync(ClaimBatch batch, string actor, CancellationToken ct)
    {
        var claimIds = await db.ClaimBatchItems.AsNoTracking()
            .Where(i => i.BatchId == batch.BatchId && i.RemovedAt == null).Select(i => i.ClaimId).ToListAsync(ct);
        var claims = await db.Claims.AsNoTracking().Include(c => c.Lines)
            .Where(c => claimIds.Contains(c.ClaimId)).ToListAsync(ct);
        var deltas = await db.ClaimAdjustments.AsNoTracking()
            .Where(a => claimIds.Contains(a.ClaimId) && !a.PendingSecondApproval)
            .GroupBy(a => a.ClaimLineId)
            .Select(g => new { LineId = g.Key, Delta = g.Sum(x => x.AmountDelta) })
            .ToDictionaryAsync(x => x.LineId, x => x.Delta, ct);

        var lines = claims.SelectMany(c => c.Lines.Select(l =>
            (c.ClaimNo, Line: l, AdjustedDelta: deltas.TryGetValue(l.ClaimLineId, out var d) ? d : 0m)));

        var priorVersion = await db.SettlementAdvices.AsNoTracking()
            .Where(a => a.BatchId == batch.BatchId).OrderByDescending(a => a.Version).Select(a => (int?)a.Version).FirstOrDefaultAsync(ct);

        return SettlementBuilder.Build(batch.BatchNo, batch.PayeeProviderId, batch.ProviderLocationId,
            batch.PeriodFrom, batch.PeriodTo, actor, clock.GetUtcNow(), (priorVersion ?? 0) + 1, lines);
    }
}
