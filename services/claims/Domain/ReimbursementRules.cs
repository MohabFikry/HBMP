namespace Mersal.Claims.Domain;

/// <summary>One OCR-extracted field: value + engine confidence 0–1 + source region (page + bounding box) for the human
/// review overlay. Shared by the pluggable <c>IDocumentOcrProvider</c> and the append-only <c>ocr_extraction</c> store.</summary>
public sealed record OcrField(string FieldName, string? Value, decimal Confidence, int? Page, string? Region);

/// <summary>The two OCR-match outcomes (23 §10). AutoMatched pre-fills lines (still unpayable until a human confirms);
/// ManualAssessment routes to hand-matching. There is NO auto-approval path — a decision is always a human's.</summary>
public enum OcrMatchOutcome { AutoMatched, ManualAssessment }

/// <summary>Pure reimbursement rules (36 §3.3, 23 §10) — kept free of infrastructure so the human-gate, confidence,
/// ambiguity, mismatch, and cap logic are unit-tested in isolation. Every rule here is literal from the design doc.</summary>
public static class ReimbursementRules
{
    /// <summary>Per-field OCR confidence threshold; a field below this forces ManualAssessment. Configurable
    /// (Claims:OcrConfidenceThreshold). OCR is assistive — a false high-confidence read must never quietly pay.</summary>
    public const decimal DefaultConfidenceThreshold = 0.90m;

    /// <summary>Decide the OCR-match outcome. AutoMatched requires ALL of: an authorized underlying order/prescription,
    /// exactly ONE candidate (0 or &gt;1 ⇒ ambiguous), no field mismatch, and every extracted field at/above the
    /// threshold. Anything else ⇒ ManualAssessment (never auto-final).</summary>
    public static OcrMatchOutcome DecideMatch(
        bool hasAuthorizedOrder, int candidateCount, bool anyMismatch,
        IReadOnlyList<decimal> fieldConfidences, decimal threshold)
    {
        ArgumentNullException.ThrowIfNull(fieldConfidences);
        if (!hasAuthorizedOrder) return OcrMatchOutcome.ManualAssessment;
        if (candidateCount != 1) return OcrMatchOutcome.ManualAssessment;      // 0 = no match, >1 = ambiguous
        if (anyMismatch) return OcrMatchOutcome.ManualAssessment;              // provider/date/amount/code disagree
        if (fieldConfidences.Count == 0) return OcrMatchOutcome.ManualAssessment;
        return fieldConfidences.All(c => c >= threshold)
            ? OcrMatchOutcome.AutoMatched : OcrMatchOutcome.ManualAssessment;
    }

    /// <summary>The payable cap: min(contract tariff, receipt). With no tariff the receipt is the ceiling — a
    /// reimbursement is never paid above what the member actually paid.</summary>
    public static decimal Cap(decimal? contractTariff, decimal receiptAmount) =>
        contractTariff is { } t ? Math.Min(t, receiptAmount) : receiptAmount;

    /// <summary>Validate a requested payable against the cap. At/under the cap → OK. Above the cap → allowed ONLY as an
    /// explicit officer override with a non-empty justification (dual control + audit are applied by the caller).
    /// Returns an error token or null.</summary>
    public static string? ValidateOverride(decimal requestedPayable, decimal cap, bool isOverride, string? justification)
    {
        if (requestedPayable <= cap) return null;
        if (!isOverride) return "exceeds-cap";
        return string.IsNullOrWhiteSpace(justification) ? "override-justification-required" : null;
    }
}
