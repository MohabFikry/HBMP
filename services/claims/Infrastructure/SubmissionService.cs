using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Infrastructure;

/// <summary>Outcome of a provider submission. <c>Created</c> recorded the submission + its ProviderSubmitted claim;
/// <c>Replayed</c> is the idempotent no-op for a retried Idempotency-Key; <c>Duplicate</c> means at least one line
/// referenced an already-claimed fulfillment (the no-double-billing index fired) → nothing is created, DUPLICATE_CLAIM.</summary>
public enum SubmitOutcome { Created, Replayed, Duplicate }

/// <summary>A provider-asserted invoice line (min-necessary — code + amount + date, no clinical field).</summary>
public sealed record SubmissionLineInput(
    ClaimCodeSystem CodeSystem, string Code, string? Description, DateOnly ServiceDate,
    decimal Quantity, decimal BilledAmount, Guid? AuthorizationId);

public sealed record SubmissionRequest(
    Guid ProviderId, Guid BeneficiaryId, string? InvoiceNumber, string CurrencyCode,
    string? SubmittedOnBehalfOf, IReadOnlyList<SubmissionLineInput> Lines);

public sealed record SubmitResult(SubmitOutcome Outcome, ClaimSubmission? Submission, Claim? Claim);

/// <summary>The provider-submitted origination channel (10b.5). Each asserted line is MATCHED to a delivered/authorized
/// fulfillment via <see cref="IFulfillmentResolver"/> (provider, beneficiary, code, service date ± tolerance, auth):
/// <list type="bullet">
/// <item><b>Matched</b> → a priced, fulfillment-anchored payable line is created on a ProviderSubmitted claim, recording
///   the provider's BILLED amount alongside the contract price; a billed ≠ contract difference sets a price-variance
///   flag for reconciliation (10b.7) — never silently accepted.</item>
/// <item><b>Unmatched</b> → a NO_FULFILLMENT_RECORD / RequiresManualReview line (no fulfillment_ref) routed to the
///   manual-assessment worklist; NEVER auto-approved at any value.</item>
/// <item><b>Duplicate</b> → a matched line whose fulfillment already has a live payable line hits the 10b.1 unique
///   index (SQLSTATE 23505); the whole submission rolls back atomically → DUPLICATE_CLAIM, no second payable line.</item>
/// </list>
/// Idempotent on the header Idempotency-Key. Tariff/fulfillment resolution happen OUTSIDE the write transaction.</summary>
public sealed class SubmissionService(
    ClaimsDbContext db, ClaimNoIssuer claimNo, IFulfillmentResolver resolver,
    IContractTariffProvider tariffs, TimeProvider clock)
{
    public async Task<SubmitResult> SubmitAsync(
        string tenantId, string actor, SubmissionRequest req, string idempotencyKey,
        string? bearerToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        // (1) Idempotent replay — a retried submission with the same key returns the first result unchanged.
        var prior = await db.ClaimSubmissions.AsNoTracking().Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.IdempotencyKey == idempotencyKey, ct);
        if (prior is not null)
        {
            var priorClaim = prior.ClaimId is { } cid
                ? await db.Claims.AsNoTracking().Include(c => c.Lines).FirstOrDefaultAsync(c => c.ClaimId == cid, ct) : null;
            return new SubmitResult(SubmitOutcome.Replayed, prior, priorClaim);
        }

        // (2) Resolve fulfillment + tariff per line OUTSIDE the write transaction (HTTP-bound work).
        var plans = new List<LinePlan>(req.Lines.Count);
        foreach (var line in req.Lines)
        {
            var key = new MatchKey(req.ProviderId, req.BeneficiaryId, line.CodeSystem, line.Code, line.AuthorizationId);
            var match = await resolver.ResolveAsync(key, line.ServiceDate, bearerToken, ct);
            if (match is null) { plans.Add(new LinePlan(line, null, null)); continue; }
            var tariff = await tariffs.ResolveAsync(
                req.ProviderId, line.CodeSystem, line.Code, line.ServiceDate, bearerToken, ct);
            plans.Add(new LinePlan(line, match, tariff));
        }

        var now = clock.GetUtcNow();
        var dates = req.Lines.Select(l => l.ServiceDate).ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var claim = new Claim
            {
                ClaimId = Guid.NewGuid(),
                ClaimNo = await claimNo.NextAsync(now.Year, ct),
                Origin = ClaimOrigin.ProviderSubmitted,
                BeneficiaryId = req.BeneficiaryId,
                ProviderId = req.ProviderId,
                TenantId = tenantId,
                ServiceDateFrom = dates.Min(),
                ServiceDateTo = dates.Max(),
                CurrencyCode = req.CurrencyCode,
                // Submitted claims land straight in adjudication so matched AND unmatched lines reach the worklist.
                Status = ClaimStatus.UnderAdjudication,
                SubmittedAt = now,
                CreatedAt = now,
                CreatedBy = actor,
            };
            db.Claims.Add(claim);

            var submission = new ClaimSubmission
            {
                SubmissionId = Guid.NewGuid(),
                ClaimId = claim.ClaimId,
                ProviderId = req.ProviderId,
                BeneficiaryId = req.BeneficiaryId,
                InvoiceNumber = req.InvoiceNumber,
                CurrencyCode = req.CurrencyCode,
                TenantId = tenantId,
                SubmittedBy = actor,
                SubmittedOnBehalfOf = req.SubmittedOnBehalfOf,
                SubmittedAt = now,
                IdempotencyKey = idempotencyKey,
            };

            foreach (var plan in plans)
            {
                var (line, subLine) = BuildLine(claim, plan);
                db.ClaimLines.Add(line);
                submission.Lines.Add(subLine);
                claim.ClaimedAmount += line.BilledAmount;
                claim.PricedAmount = (claim.PricedAmount ?? 0) + (line.ContractPrice ?? 0);
            }

            submission.Status = DeriveStatus(submission.Lines);
            db.ClaimSubmissions.Add(submission);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new SubmitResult(SubmitOutcome.Created, submission, claim);
        }
        catch (DbUpdateException ex) when (ConstraintOf(ex) == "ux_claim_line_fulfillment")
        {
            // A matched line pointed at a fulfillment that already has a live payable line → atomic reject, nothing created.
            await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
            return new SubmitResult(SubmitOutcome.Duplicate, null, null);
        }
        catch (DbUpdateException ex) when (ConstraintOf(ex) == "ux_submission_idempotency")
        {
            // Concurrent submit with the same key won the race → return theirs.
            await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
            var won = await db.ClaimSubmissions.AsNoTracking().Include(s => s.Lines)
                .FirstAsync(s => s.IdempotencyKey == idempotencyKey, ct);
            return new SubmitResult(SubmitOutcome.Replayed, won, null);
        }
    }

    /// <summary>Attach a document reference to a submission's claim (10b.5). The bytes live in document-service
    /// (scanned + encrypted); we store only the reference. Idempotent on (claim, document) via the unique index.</summary>
    public async Task<ClaimDocument?> AttachDocumentAsync(
        Guid submissionId, string tenantId, Guid documentId, ClaimDocType docType, string actor, CancellationToken ct = default)
    {
        var submission = await db.ClaimSubmissions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SubmissionId == submissionId && s.TenantId == tenantId, ct);
        if (submission?.ClaimId is not { } claimId) return null;

        var existing = await db.ClaimDocuments
            .FirstOrDefaultAsync(d => d.ClaimId == claimId && d.DocumentId == documentId, ct);
        if (existing is not null) return existing;

        var doc = new ClaimDocument
        {
            ClaimDocumentId = Guid.NewGuid(), ClaimId = claimId, DocumentId = documentId,
            DocType = docType, LinkedBy = actor, LinkedAt = clock.GetUtcNow(),
        };
        db.ClaimDocuments.Add(doc);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ConstraintOf(ex) == "ux_claim_document_claim")
        {
            db.ChangeTracker.Clear();
            return await db.ClaimDocuments.AsNoTracking()
                .FirstAsync(d => d.ClaimId == claimId && d.DocumentId == documentId, ct);
        }
        return doc;
    }

    // ---- line construction --------------------------------------------------------------------------------
    private readonly record struct LinePlan(SubmissionLineInput Input, FulfillmentMatch? Match, decimal? Tariff);

    private static (ClaimLine, ClaimSubmissionLine) BuildLine(Claim claim, LinePlan plan)
    {
        var lineId = Guid.NewGuid();
        var input = plan.Input;

        if (plan.Match is { } match)
        {
            // Matched: a priced payable line anchored to the fulfillment. Record billed ALONGSIDE contract; a
            // billed ≠ contract difference is a reconciliation candidate (variance flag), never silently accepted.
            var (price, recommendation, reasons) = AutoDerivePricing.Price(plan.Tariff);
            var variance = price is not null && input.BilledAmount != price;
            var line = new ClaimLine
            {
                ClaimLineId = lineId, ClaimId = claim.ClaimId,
                FulfillmentRef = match.FulfillmentRef, FulfillmentType = match.FulfillmentType,
                CodeSystem = input.CodeSystem, Code = input.Code, Description = input.Description,
                Quantity = input.Quantity, BilledAmount = input.BilledAmount, ContractPrice = price,
                Status = ClaimLineStatus.Pending, SystemRecommendation = recommendation,
                ReasonCodes = [.. reasons], RuleVersion = AutoDerivePricing.RuleVersion,
                AuthorizationId = input.AuthorizationId,
            };
            var subLine = NewSubLine(input, lineId, SubmissionLineOutcome.Matched, variance,
                reasons.Count > 0 ? reasons[0] : null);
            return (line, subLine);
        }

        // Unmatched: no fulfillment record → manual assessment, never auto-approved. No fulfillment_ref, no price.
        var manual = new ClaimLine
        {
            ClaimLineId = lineId, ClaimId = claim.ClaimId,
            FulfillmentRef = null, FulfillmentType = FulfillmentType.None,
            CodeSystem = input.CodeSystem, Code = input.Code, Description = input.Description,
            Quantity = input.Quantity, BilledAmount = input.BilledAmount, ContractPrice = null,
            Status = ClaimLineStatus.Pending, SystemRecommendation = SystemRecommendation.RequiresManualReview,
            ReasonCodes = [ReasonCodes.NoFulfillmentRecord], RuleVersion = AutoDerivePricing.RuleVersion,
            AuthorizationId = input.AuthorizationId,
        };
        return (manual, NewSubLine(input, lineId, SubmissionLineOutcome.Unmatched, false, ReasonCodes.NoFulfillmentRecord));
    }

    private static ClaimSubmissionLine NewSubLine(
        SubmissionLineInput input, Guid claimLineId, SubmissionLineOutcome outcome, bool variance, string? reason) => new()
    {
        SubmissionLineId = Guid.NewGuid(), CodeSystem = input.CodeSystem, Code = input.Code,
        Description = input.Description, ServiceDate = input.ServiceDate, Quantity = input.Quantity,
        BilledAmount = input.BilledAmount, AuthorizationId = input.AuthorizationId,
        Outcome = outcome, ClaimLineId = claimLineId, PriceVariance = variance, ReasonCode = reason,
    };

    private static SubmissionStatus DeriveStatus(IReadOnlyCollection<ClaimSubmissionLine> lines)
    {
        var matched = lines.Count(l => l.Outcome == SubmissionLineOutcome.Matched);
        if (matched == lines.Count) return SubmissionStatus.Matched;
        return matched == 0 ? SubmissionStatus.Unmatched : SubmissionStatus.PartiallyMatched;
    }

    private static string? ConstraintOf(DbUpdateException ex)
    {
        for (Exception? e = ex.InnerException; e is not null; e = e.InnerException)
            if (e.GetType().GetProperty("SqlState")?.GetValue(e) as string == "23505")
                return e.GetType().GetProperty("ConstraintName")?.GetValue(e) as string ?? "";
        return null;
    }
}
