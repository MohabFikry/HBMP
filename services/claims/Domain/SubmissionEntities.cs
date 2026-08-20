namespace Mersal.Claims.Domain;

/// <summary>A provider-submitted invoice/claim intake record (10b.5, 22 §10A minimum-necessary — codes + amounts only).
/// The submission is the raw, immutable record of what a provider (or Mersal on their behalf) asserted; matching turns
/// each line into a payable <c>claim_line</c> on a ProviderSubmitted claim (matched) or a manual-assessment line
/// (unmatched). <c>SubmittedOnBehalfOf</c> is set when a Mersal user submits for a provider (never null-washed —
/// accountability is recorded). Never mutated: corrections are new submissions / adjustments.</summary>
public sealed class ClaimSubmission
{
    public Guid SubmissionId { get; set; }
    /// <summary>The ProviderSubmitted claim raised from this submission (set once matching has run).</summary>
    public Guid? ClaimId { get; set; }
    public Guid ProviderId { get; set; }
    public Guid BeneficiaryId { get; set; }
    public string? InvoiceNumber { get; set; }
    public string CurrencyCode { get; set; } = "EGP";
    public SubmissionStatus Status { get; set; } = SubmissionStatus.Received;
    public string TenantId { get; set; } = default!;
    /// <summary>Actor who physically submitted (a provider user, or a Mersal user acting for them).</summary>
    public string SubmittedBy { get; set; } = default!;
    /// <summary>Provider the submission is FOR, when a Mersal user submits on their behalf; else null (self-submit).</summary>
    public string? SubmittedOnBehalfOf { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    /// <summary>Idempotency key (header) — a retried submission with the same key is a no-op returning the first result.</summary>
    public string IdempotencyKey { get; set; } = default!;

    /// <summary>SHA-256 of the canonical request this key produced (migration 0009). Without it a key reused
    /// across two invoices returned the first claim, telling the provider their second invoice had been
    /// received when nothing had been. NULL on pre-0009 rows: treated as a match.</summary>
    public string? RequestHash { get; set; }

    public List<ClaimSubmissionLine> Lines { get; set; } = [];
}

/// <summary>One asserted line of a provider submission and its matching outcome (10b.5). Carries the provider's
/// billed amount; the matched payable line records that billed amount ALONGSIDE the contract price so a
/// billed ≠ contract difference is a price-variance candidate for reconciliation (10b.7), never silently accepted.</summary>
public sealed class ClaimSubmissionLine
{
    public Guid SubmissionLineId { get; set; }
    public Guid SubmissionId { get; set; }
    public ClaimCodeSystem CodeSystem { get; set; }
    public string Code { get; set; } = default!;
    public string? Description { get; set; }
    public DateOnly ServiceDate { get; set; }
    public decimal Quantity { get; set; }
    public decimal BilledAmount { get; set; }
    public Guid? AuthorizationId { get; set; }
    // ---- matching result -----------------------------------------------------------------------------------
    public SubmissionLineOutcome Outcome { get; set; }
    /// <summary>The payable <c>claim_line</c> created for this asserted line (matched OR manual). Null on Duplicate.</summary>
    public Guid? ClaimLineId { get; set; }
    /// <summary>True when the provider's billed amount differs from the contract price — a reconciliation candidate.</summary>
    public bool PriceVariance { get; set; }
    /// <summary>Coded reason recorded on a non-matched line (e.g. NO_FULFILLMENT_RECORD, DUPLICATE_CLAIM).</summary>
    public string? ReasonCode { get; set; }
}

/// <summary>A link from a claim (or a reimbursement request) to a document held in document-service (22 §10A.7).
/// Claims-service stores only the REFERENCE — never the bytes. ResultProof/DispenseProof prove a service EXISTED
/// (date + reference); claims roles never read the clinical content.</summary>
public sealed class ClaimDocument
{
    public Guid ClaimDocumentId { get; set; }
    public Guid? ClaimId { get; set; }
    public Guid? RequestId { get; set; }
    public Guid DocumentId { get; set; }
    public ClaimDocType DocType { get; set; }
    public string LinkedBy { get; set; } = default!;
    public DateTimeOffset LinkedAt { get; set; }
}
