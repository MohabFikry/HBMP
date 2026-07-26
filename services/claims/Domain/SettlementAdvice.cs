namespace Mersal.Claims.Domain;

/// <summary>An immutable settlement advice / remittance statement for a decided batch (22 §10A.5, 36 §8). Append-only:
/// regeneration NEVER overwrites — it writes a NEW version referencing the one it supersedes. The document itself is
/// stored WORM in document-service; this row is the append-only ledger entry (document reference + content hash +
/// totals snapshot + batch link). <b>The platform never moves money</b> — this is the hand-off artifact to Finance.</summary>
public sealed class SettlementAdvice
{
    public Guid AdviceId { get; set; }
    public Guid BatchId { get; set; }
    public string TenantId { get; set; } = default!;
    public string BatchNo { get; set; } = default!;
    public Guid? PayeeProviderId { get; set; }
    public Guid? ProviderLocationId { get; set; }
    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }
    public int Version { get; set; } = 1;
    /// <summary>The prior advice this one supersedes (a regeneration), else null for the first.</summary>
    public Guid? SupersedesAdviceId { get; set; }
    /// <summary>Reference to the WORM document in document-service (object-lock / retention).</summary>
    public Guid? DocumentId { get; set; }
    /// <summary>SHA-256 of the canonical rendering — proves the stored document has not changed.</summary>
    public string ContentHash { get; set; } = default!;
    // frozen totals snapshot
    public decimal TotalClaimed { get; set; }
    public decimal TotalPriced { get; set; }
    public decimal TotalApproved { get; set; }
    public decimal TotalAdjusted { get; set; }
    public decimal TotalDenied { get; set; }
    public decimal NetPayable { get; set; }
    public string GeneratedBy { get; set; } = default!;
    public DateTimeOffset GeneratedAt { get; set; }
}

/// <summary>An append-only record of an EXTERNAL payment made by Finance/treasury outside the platform (36 §8). This
/// RECORDS a fact — it initiates nothing. The platform has no payout endpoint or payment rail.</summary>
public sealed class SettlementPaymentReference
{
    public Guid PaymentReferenceId { get; set; }
    public Guid BatchId { get; set; }
    public string TenantId { get; set; } = default!;
    public string Reference { get; set; } = default!;
    public DateOnly PaymentDate { get; set; }
    public string RecordedBy { get; set; } = default!;
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>One settlement-advice detail row — min-necessary, ZERO clinical fields (codes + amounts + coded reasons).</summary>
public sealed record SettlementLineRow(
    string ClaimNo, string CodeSystem, string Code, decimal Quantity, decimal BilledAmount,
    decimal? ContractPrice, decimal? AllowedAmount, decimal AdjustedDelta, string LineStatus,
    IReadOnlyList<string> ReasonCodes);

/// <summary>The full settlement-advice projection (header + detail + totals). Pure — built from batch data and rendered
/// to CSV/XLSX/PDF identically. It is structurally incapable of carrying a clinical field.</summary>
public sealed record SettlementProjection(
    string BatchNo, Guid? PayeeProviderId, Guid? ProviderLocationId, DateOnly PeriodFrom, DateOnly PeriodTo,
    string GeneratedBy, DateTimeOffset GeneratedAt, int Version, IReadOnlyList<SettlementLineRow> Lines,
    decimal TotalClaimed, decimal TotalPriced, decimal TotalApproved, decimal TotalAdjusted,
    decimal TotalDenied, decimal NetPayable);
