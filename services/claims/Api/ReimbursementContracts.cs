using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;

namespace Mersal.Claims.Api;

/// <summary>Submit a beneficiary reimbursement (10b.6). The member, or Reception / a Case Manager on their behalf,
/// supplies the underlying AUTHORIZED order/prescription reference, the receipt total, and the receipt + result/dispense
/// evidence documents (already stored in document-service). NO bank/payout detail is accepted or stored.</summary>
public sealed record ReimbursementRequestBody(
    Guid BeneficiaryId, decimal ReceiptTotal, string? CurrencyCode,
    Guid? LinkedOrderId, Guid? LinkedPrescriptionId, IReadOnlyList<ReimbursementDocBody> Documents);

public sealed record ReimbursementDocBody(Guid DocumentId, string DocType, string? ContentType, long SizeBytes);

/// <summary>Human confirmation of an auto-matched request, or a manual match — supplies the order/prescription the
/// reviewer links by hand. Confirmation records acceptance of the OCR values and creates the (still Pending) claim.</summary>
public sealed record ReimbursementConfirmBody(Guid? LinkedOrderId, Guid? LinkedPrescriptionId);

/// <summary>Min-necessary reimbursement projection with the OCR overlay data (value + confidence + region) so a human
/// can verify against the document image. No bank/payout field; no clinical value.</summary>
public sealed record ReimbursementView(
    Guid RequestId, Guid? ClaimId, Guid BeneficiaryId, string? ActingFor, decimal ReceiptTotal, string CurrencyCode,
    string Status, decimal? MatchConfidence, string MatchMethod, Guid? LinkedOrderId, Guid? LinkedPrescriptionId,
    IReadOnlyList<OcrFieldView> Ocr)
{
    public static ReimbursementView From(ReimbursementRequest r, IEnumerable<OcrExtraction> ocr) => new(
        r.RequestId, r.ClaimId, r.BeneficiaryId, r.ActingFor, r.ReceiptTotal, r.CurrencyCode, r.Status.ToString(),
        r.MatchConfidence, r.MatchMethod.ToString(), r.LinkedOrderId, r.LinkedPrescriptionId,
        ocr.Select(OcrFieldView.From).ToList());
}

/// <summary>One OCR extraction for the review overlay — field, value, confidence, page/region, and whether a human has
/// accepted it. The extracted value is a billing candidate (provider/date/amount/code), never a clinical value.</summary>
public sealed record OcrFieldView(
    string FieldName, string? Value, decimal Confidence, int? Page, string? Region, bool Accepted)
{
    public static OcrFieldView From(OcrExtraction x) =>
        new(x.FieldName, x.ExtractedValue, x.Confidence, x.Page, x.Region, x.AcceptedBy is not null);
}
