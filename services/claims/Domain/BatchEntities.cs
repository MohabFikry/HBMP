namespace Mersal.Claims.Domain;

/// <summary>A batch — the unit of review and settlement for one payee (22 §10A.5, 23 §9). Rollup totals are
/// recomputed on every membership/decision change and FROZEN at <see cref="BatchStatus.SettlementIssued"/>.</summary>
public sealed class ClaimBatch
{
    public Guid BatchId { get; set; }
    public string BatchNo { get; set; } = default!;
    public BatchType BatchType { get; set; }
    public BatchSelectionMode SelectionMode { get; set; }
    public Guid? PayeeProviderId { get; set; }
    public Guid? ProviderLocationId { get; set; }
    public string TenantId { get; set; } = default!;
    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }
    public BatchStatus Status { get; set; } = BatchStatus.Open;
    public decimal TotalClaimed { get; set; }
    public decimal TotalPriced { get; set; }
    public decimal TotalApproved { get; set; }
    public decimal TotalAdjusted { get; set; }
    public decimal TotalDenied { get; set; }
    public decimal NetPayable { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? FrozenAt { get; set; }
    public Guid? SettlementDocumentId { get; set; }
    public uint RowVersion { get; set; }

    public List<ClaimBatchItem> Items { get; set; } = [];
}

/// <summary>Batch membership — recorded, never deleted (removal sets removed_at + reason). <c>BatchStatusSnapshot</c>
/// is materialized from the parent batch so the partial unique index can enforce "one open batch per claim" purely at
/// the database (a claim can never sit in two Open/UnderReview batches, so it can never be settled twice).</summary>
public sealed class ClaimBatchItem
{
    public Guid BatchItemId { get; set; }
    public Guid BatchId { get; set; }
    public Guid ClaimId { get; set; }
    public string? AddedBy { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public string? RemovedBy { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public string? RemovalReason { get; set; }
    /// <summary>Materialized copy of the parent batch's status — drives the single-open-batch partial unique index.</summary>
    public BatchStatus BatchStatusSnapshot { get; set; } = BatchStatus.Open;
}
