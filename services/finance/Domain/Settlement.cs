namespace Mersal.Finance.Domain;

/// <summary>Settlement lifecycle. Draft → Submitted → Approved → Paid. Submit/approve are split for segregation of
/// duties (the approver must be a different principal than the submitter). No payment is executed here — Paid is a
/// recorded outcome, not a money movement (10.2 "no payment execution").</summary>
public enum SettlementStatus { Draft, Submitted, Approved, Paid }

/// <summary>A provider settlement for a period: the priced roll-up of delivered services. Prices come from the
/// provider-service <c>provider_contract</c> / <c>contract_service_line</c> agreed prices (22 §5.3) — READ, never
/// owned or mutated here.</summary>
public sealed class Settlement
{
    public Guid SettlementId { get; set; }
    public string SettlementNo { get; set; } = default!;    // STL-YYYY-NNNNNN
    public string TenantId { get; set; } = default!;
    public Guid ProviderId { get; set; }
    public Guid? ContractId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public string CurrencyCode { get; set; } = "EGP";
    public decimal Total { get; set; }
    public SettlementStatus Status { get; set; } = SettlementStatus.Draft;
    public string? SubmittedBy { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint RowVersion { get; set; }                    // xmin — optimistic concurrency (racing approvers)
    public List<SettlementLine> Lines { get; set; } = [];
}

/// <summary>A priced settlement line — a billing service code, delivered quantity, the agreed unit price READ from
/// the provider contract, and the line total. No clinical field.</summary>
public sealed class SettlementLine
{
    public Guid SettlementLineId { get; set; }
    public string TenantId { get; set; } = "";              // RLS tenant scope (ADR-0011)
    public Guid SettlementId { get; set; }
    public string ServiceCode { get; set; } = default!;     // billing code (CPT/LOINC/ATC)
    public string ServiceLine { get; set; } = "General";
    public int DeliveredQty { get; set; }
    public decimal AgreedUnitPrice { get; set; }            // from provider_contract (read, not owned)
    public decimal LineTotal { get; set; }
}

/// <summary>Business-key formatter for settlements (0A §3). STL-YYYY-NNNNNN — consistent with the platform's other
/// per-year business keys.</summary>
public static class SettlementNo
{
    public static string Format(int year, int sequence) => $"STL-{year:D4}-{sequence:D6}";
}
