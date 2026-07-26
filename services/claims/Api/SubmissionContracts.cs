using Mersal.Claims.Domain;

namespace Mersal.Claims.Api;

/// <summary>Provider-submission request. A provider user submits for their OWN provider (enforced); a Mersal user may
/// submit on a provider's behalf (recorded as <c>submitted_on_behalf_of</c>). Min-necessary — codes + amounts + dates,
/// no clinical field.</summary>
public sealed record SubmissionRequestBody(
    Guid ProviderId, Guid BeneficiaryId, string? InvoiceNumber, string? CurrencyCode,
    IReadOnlyList<SubmissionLineBody> Lines);

public sealed record SubmissionLineBody(
    string CodeSystem, string Code, string? Description, DateOnly ServiceDate,
    decimal Quantity, decimal BilledAmount, Guid? AuthorizationId);

/// <summary>Attach a document already stored (scanned + encrypted) in document-service. Claims-service records only the
/// reference + declared type/size; it never receives the bytes.</summary>
public sealed record AttachDocumentBody(Guid DocumentId, string DocType, string? ContentType, long SizeBytes);

/// <summary>Min-necessary submission projection — the provider's asserted lines and their matching outcomes; no
/// clinical field. <c>ClaimLineId</c> links a matched/manual line to the payable claim line for follow-through.</summary>
public sealed record SubmissionView(
    Guid SubmissionId, Guid? ClaimId, Guid ProviderId, Guid BeneficiaryId, string? InvoiceNumber,
    string CurrencyCode, string Status, string? SubmittedOnBehalfOf, DateTimeOffset SubmittedAt,
    IReadOnlyList<SubmissionLineView> Lines)
{
    public static SubmissionView From(ClaimSubmission s) => new(
        s.SubmissionId, s.ClaimId, s.ProviderId, s.BeneficiaryId, s.InvoiceNumber, s.CurrencyCode,
        s.Status.ToString(), s.SubmittedOnBehalfOf, s.SubmittedAt,
        s.Lines.Select(SubmissionLineView.From).ToList());
}

public sealed record SubmissionLineView(
    Guid SubmissionLineId, string CodeSystem, string Code, string? Description, DateOnly ServiceDate,
    decimal Quantity, decimal BilledAmount, string Outcome, Guid? ClaimLineId, bool PriceVariance, string? ReasonCode)
{
    public static SubmissionLineView From(ClaimSubmissionLine l) => new(
        l.SubmissionLineId, l.CodeSystem.ToString(), l.Code, l.Description, l.ServiceDate, l.Quantity,
        l.BilledAmount, l.Outcome.ToString(), l.ClaimLineId, l.PriceVariance, l.ReasonCode);
}
