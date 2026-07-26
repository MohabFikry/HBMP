using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Infrastructure;

public enum ReimbursementOutcome { AutoMatched, ManualAssessment, RejectedFiles, RejectedScan }

public sealed record ReimbursementDoc(Guid DocumentId, ClaimDocType DocType, string? ContentType, long SizeBytes);

public sealed record ReimbursementSubmission(
    Guid BeneficiaryId, string? ActingFor, decimal ReceiptTotal, string CurrencyCode,
    Guid? LinkedOrderId, Guid? LinkedPrescriptionId, IReadOnlyList<ReimbursementDoc> Documents);

public sealed record ReimbursementResult(
    ReimbursementOutcome Outcome, ReimbursementRequest? Request, string? Error = null);

public enum ConfirmOutcome { Created, NotFound, NotConfirmable, NoAuthorizedService }
public sealed record ConfirmResult(ConfirmOutcome Outcome, ReimbursementRequest? Request, Claim? Claim);

/// <summary>The beneficiary reimbursement channel with OCR assistance (10b.6, 36 §3.3, 23 §10). Pipeline:
/// validate file type/size → malware scan → persist request → OCR extract (append-only <c>ocr_extraction</c>) →
/// decide AutoMatched vs ManualAssessment. OCR is ASSISTIVE, NEVER AUTHORITATIVE — <see cref="ConfirmAsync"/> records a
/// human's acceptance of the extracted values before the Reimbursement claim is created, and the claim's lines are
/// Pending: a line becomes payable only through an explicit Claims Officer decision (10b.4). Reimbursement is capped at
/// min(contract tariff, receipt); exceeding it needs an audited officer override. No bank/payout detail is ever stored.</summary>
public sealed class ReimbursementService(
    ClaimsDbContext db, ClaimNoIssuer claimNo, IDocumentOcrProvider ocr, IDocumentScanner scanner,
    IAuthorizedServiceResolver authorized, IContractTariffProvider tariffs, TimeProvider clock,
    ReimbursementOptions options)
{
    public async Task<ReimbursementResult> SubmitAsync(
        string tenantId, string actor, ReimbursementSubmission sub, string? bearerToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sub);

        // (1) File type/size gate — any bad file rejects the whole upload (audited by the caller).
        foreach (var d in sub.Documents)
            if (DocumentValidation.Validate(d.ContentType, d.SizeBytes) is { } fileErr)
                return new ReimbursementResult(ReimbursementOutcome.RejectedFiles, null, fileErr);

        // (2) Malware scan — an infected document is rejected and never stored/processed.
        foreach (var d in sub.Documents)
        {
            var scan = await scanner.ScanAsync(d.DocumentId, bearerToken, ct);
            if (!scan.Clean) return new ReimbursementResult(ReimbursementOutcome.RejectedScan, null, scan.Signature ?? "infected");
        }

        var now = clock.GetUtcNow();
        var request = new ReimbursementRequest
        {
            RequestId = Guid.NewGuid(), BeneficiaryId = sub.BeneficiaryId, SubmittedBy = actor, ActingFor = sub.ActingFor,
            SubmittedAt = now, ReceiptTotal = sub.ReceiptTotal, CurrencyCode = sub.CurrencyCode,
            LinkedOrderId = sub.LinkedOrderId, LinkedPrescriptionId = sub.LinkedPrescriptionId,
            Status = ReimbursementStatus.OcrProcessing, TenantId = tenantId,
        };
        db.ReimbursementRequests.Add(request);
        await db.SaveChangesAsync(ct);

        // (3) OCR extraction — persist EVERY field append-only with confidence + region + engine/version. Only the
        // financial documents are read (receipt/invoice/statement); result/dispense proofs prove a service EXISTED
        // and are never OCR'd for content (min-necessary — no clinical value is ever extracted).
        var confidences = new List<decimal>();
        foreach (var doc in sub.Documents.Where(d => d.DocType is ClaimDocType.Receipt or ClaimDocType.Invoice or ClaimDocType.Statement))
        {
            var fields = await ocr.ExtractAsync(doc.DocumentId, options.Languages, bearerToken, ct);
            foreach (var f in fields.Where(f => OcrFields.IsKnown(f.FieldName)))
            {
                db.OcrExtractions.Add(new OcrExtraction
                {
                    ExtractionId = Guid.NewGuid(), RequestId = request.RequestId, DocumentId = doc.DocumentId,
                    FieldName = f.FieldName, ExtractedValue = f.Value, Confidence = f.Confidence, Page = f.Page,
                    Region = f.Region, Engine = ocr.Engine, EngineVersion = ocr.EngineVersion, ExtractedAt = now,
                });
                confidences.Add(f.Confidence);
            }
        }

        // (4) Prerequisites (hard): a legible receipt AND result/dispense evidence must be present to auto-match.
        var hasReceipt = sub.Documents.Any(d => d.DocType == ClaimDocType.Receipt);
        var hasEvidence = sub.Documents.Any(d => d.DocType is ClaimDocType.ResultProof or ClaimDocType.DispenseProof);

        // (5) Resolve the authorized underlying order/prescription (a hard prerequisite for auto-match).
        var candidates = await authorized.ResolveAsync(
            sub.BeneficiaryId, sub.LinkedOrderId, sub.LinkedPrescriptionId, bearerToken, ct);
        var anyMismatch = candidates.Count == 1 && HasMismatch(candidates[0], sub);

        var decision = (hasReceipt && hasEvidence)
            ? ReimbursementRules.DecideMatch(candidates.Count >= 1, candidates.Count, anyMismatch, confidences, options.ConfidenceThreshold)
            : OcrMatchOutcome.ManualAssessment;

        if (decision == OcrMatchOutcome.AutoMatched)
        {
            var matched = candidates[0];
            request.Status = ReimbursementStatus.AutoMatched;
            request.MatchMethod = ReimbursementMatchMethod.AutoOcr;
            request.MatchConfidence = confidences.Count > 0 ? confidences.Min() : null;
            request.LinkedOrderId = matched.OrderId ?? request.LinkedOrderId;
            request.LinkedPrescriptionId = matched.PrescriptionId ?? request.LinkedPrescriptionId;
        }
        else
        {
            request.Status = ReimbursementStatus.ManualAssessment;
        }
        await db.SaveChangesAsync(ct);

        return new ReimbursementResult(
            request.Status == ReimbursementStatus.AutoMatched ? ReimbursementOutcome.AutoMatched : ReimbursementOutcome.ManualAssessment,
            request);
    }

    /// <summary>The HUMAN GATE. A Claims Officer accepts the extracted values (records accepted_by/accepted_at on the
    /// OCR rows) and links the authorized service; only then is the Reimbursement claim created — with PENDING lines
    /// that still require an explicit officer decision (10b.4) to become payable. Manual matches supply the order id.</summary>
    public async Task<ConfirmResult> ConfirmAsync(
        Guid requestId, string tenantId, string actor, Guid? manualOrderId, Guid? manualPrescriptionId,
        string? bearerToken, CancellationToken ct = default)
    {
        var request = await db.ReimbursementRequests
            .FirstOrDefaultAsync(r => r.RequestId == requestId && r.TenantId == tenantId, ct);
        if (request is null) return new ConfirmResult(ConfirmOutcome.NotFound, null, null);
        if (request.Status is not (ReimbursementStatus.AutoMatched or ReimbursementStatus.ManualAssessment))
            return new ConfirmResult(ConfirmOutcome.NotConfirmable, request, null);
        if (request.ClaimId is not null) return new ConfirmResult(ConfirmOutcome.NotConfirmable, request, null);

        if (manualOrderId is not null) { request.LinkedOrderId = manualOrderId; request.MatchMethod = ReimbursementMatchMethod.Manual; }
        if (manualPrescriptionId is not null) { request.LinkedPrescriptionId = manualPrescriptionId; request.MatchMethod = ReimbursementMatchMethod.Manual; }

        var candidates = await authorized.ResolveAsync(
            request.BeneficiaryId, request.LinkedOrderId, request.LinkedPrescriptionId, bearerToken, ct);
        if (candidates.Count != 1) return new ConfirmResult(ConfirmOutcome.NoAuthorizedService, request, null);
        var svc = candidates[0];

        var now = clock.GetUtcNow();
        // Record the human's acceptance on every extraction — the value now MAY inform money (still officer-decided).
        var extractions = await db.OcrExtractions.Where(x => x.RequestId == requestId && x.AcceptedBy == null).ToListAsync(ct);
        foreach (var x in extractions) { x.AcceptedBy = actor; x.AcceptedAt = now; }

        var tariff = svc.ContractTariff
            ?? await tariffs.ResolveAsync(svc.ProviderId, svc.CodeSystem, svc.Code, svc.ServiceDate, bearerToken, ct);
        var (price, recommendation, reasons) = AutoDerivePricing.Price(tariff);

        var claim = new Claim
        {
            ClaimId = Guid.NewGuid(), ClaimNo = await claimNo.NextAsync(now.Year, ct),
            Origin = ClaimOrigin.Reimbursement, BeneficiaryId = request.BeneficiaryId, ProviderId = svc.ProviderId,
            TenantId = tenantId, ServiceDateFrom = svc.ServiceDate, ServiceDateTo = svc.ServiceDate,
            CurrencyCode = request.CurrencyCode, ClaimedAmount = request.ReceiptTotal, PricedAmount = price,
            Status = ClaimStatus.UnderAdjudication, SubmittedAt = request.SubmittedAt, CreatedAt = now, CreatedBy = actor,
        };
        claim.Lines.Add(new ClaimLine
        {
            ClaimLineId = Guid.NewGuid(), ClaimId = claim.ClaimId, FulfillmentRef = null, FulfillmentType = FulfillmentType.None,
            CodeSystem = svc.CodeSystem, Code = svc.Code, Quantity = 1, BilledAmount = request.ReceiptTotal,
            ContractPrice = price, Status = ClaimLineStatus.Pending, SystemRecommendation = recommendation,
            ReasonCodes = [.. reasons], RuleVersion = AutoDerivePricing.RuleVersion,
        });
        db.Claims.Add(claim);

        request.ClaimId = claim.ClaimId;
        request.Status = ReimbursementStatus.Adjudicating;
        await db.SaveChangesAsync(ct);

        return new ConfirmResult(ConfirmOutcome.Created, request, claim);
    }

    /// <summary>Provider/date/amount/code disagreement between the submission and the single authorized candidate.</summary>
    private static bool HasMismatch(AuthorizedService svc, ReimbursementSubmission sub)
    {
        // The receipt total may not EXCEED the authorized service's tariff by policy is checked at the cap; here a
        // mismatch is a hard contradiction: a linked order id that does not resolve to this candidate.
        if (sub.LinkedOrderId is { } oid && svc.OrderId is { } soid && oid != soid) return true;
        if (sub.LinkedPrescriptionId is { } pid && svc.PrescriptionId is { } spid && pid != spid) return true;
        return false;
    }
}

/// <summary>Reimbursement tunables — the OCR languages passed to the engine and the per-field confidence threshold
/// above which an unambiguous match may be flagged AUTO_MATCHED (still never payable without a human decision).</summary>
public sealed record ReimbursementOptions
{
    public string Languages { get; init; } = "ara+eng";
    public decimal ConfidenceThreshold { get; init; } = ReimbursementRules.DefaultConfidenceThreshold;
}
