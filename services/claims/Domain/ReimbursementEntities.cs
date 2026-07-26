namespace Mersal.Claims.Domain;

/// <summary>A beneficiary out-of-pocket reimbursement request (22 §10A.6, 36 §3.3). The member — or Reception / a Case
/// Manager on their behalf (<c>ActingFor</c>) — submits receipts + result/dispense evidence against an AUTHORIZED
/// underlying order/prescription. NO bank/payout details are ever stored here: payout runs through Mersal's existing
/// finance process; the settlement advice references the member only. Capped at min(contract tariff, receipt).</summary>
public sealed class ReimbursementRequest
{
    public Guid RequestId { get; set; }
    /// <summary>The Reimbursement-origin claim raised once a human confirms the match (set on confirm).</summary>
    public Guid? ClaimId { get; set; }
    public Guid BeneficiaryId { get; set; }
    public string SubmittedBy { get; set; } = default!;
    /// <summary>Set when Reception / a Case Manager submits for the member; else null (member self-submit).</summary>
    public string? ActingFor { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public decimal ReceiptTotal { get; set; }
    public string CurrencyCode { get; set; } = "EGP";
    public ReimbursementStatus Status { get; set; } = ReimbursementStatus.Submitted;
    /// <summary>Auto-match confidence 0–1; below threshold ⇒ ManualAssessment.</summary>
    public decimal? MatchConfidence { get; set; }
    public ReimbursementMatchMethod MatchMethod { get; set; } = ReimbursementMatchMethod.Unmatched;
    /// <summary>The authorized underlying order — one of order/prescription is required to auto-match.</summary>
    public Guid? LinkedOrderId { get; set; }
    public Guid? LinkedPrescriptionId { get; set; }
    public string TenantId { get; set; } = default!;
}

/// <summary>An append-only OCR extraction (22 §10A.8). One row per extracted field per run — NEVER overwritten. A value
/// affects money ONLY once a human sets <c>AcceptedBy</c>/<c>AcceptedAt</c> (OCR is assistive, never authoritative).
/// <c>Region</c> is the bounding-box JSON for the review overlay so the officer can verify against the document image.</summary>
public sealed class OcrExtraction
{
    public Guid ExtractionId { get; set; }
    public Guid RequestId { get; set; }
    public Guid DocumentId { get; set; }
    public string FieldName { get; set; } = default!;
    public string? ExtractedValue { get; set; }
    public decimal Confidence { get; set; }
    public int? Page { get; set; }
    public string? Region { get; set; }
    public string Engine { get; set; } = default!;
    public string EngineVersion { get; set; } = default!;
    public DateTimeOffset ExtractedAt { get; set; }
    public string? AcceptedBy { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
}

/// <summary>The canonical OCR field-name allow-list (36 §3.3 "Extract candidates: provider, date, amount, currency,
/// drug/service codes"). Extraction rows outside this set are rejected.</summary>
public static class OcrFields
{
    public const string Provider = "provider";
    public const string ServiceDate = "service_date";
    public const string Amount = "amount";
    public const string Currency = "currency";
    public const string Code = "code";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Provider, ServiceDate, Amount, Currency, Code,
    };

    public static bool IsKnown(string field) => All.Contains(field);
}
