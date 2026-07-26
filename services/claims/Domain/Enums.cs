namespace Mersal.Claims.Domain;

// Enums are persisted as their string names (HasConversion<string>()) and CHECK-constrained in SQL to the exact
// value sets in 22-data-dictionary §11.5. Never renumber or rename — the DB constraint and the wire contract both
// depend on these spellings.

/// <summary>Origination channel of a claim (22 §11.5 "Claim origin").</summary>
public enum ClaimOrigin { AutoDerived, ProviderSubmitted, Reimbursement }

/// <summary>Discriminator for <c>claim_line.fulfillment_ref</c> (22 §11.5 "Fulfillment type").
/// <c>None</c> ⇒ the reference is null (an unmatched submitted/reimbursement line).</summary>
public enum FulfillmentType { OrderFulfillment, DispenseEvent, None }

/// <summary>Coding system of a claim line's service/drug code (22 §11.5).</summary>
public enum ClaimCodeSystem { CPT, LOINC, LOCAL, DRUG }

/// <summary>Pre-adjudication output per line (22 §11.5). The system RECOMMENDS; the Claims Officer decides.</summary>
public enum SystemRecommendation { RecommendApprove, RecommendPartial, RecommendDeny, RequiresManualReview }

/// <summary>A Claims Officer's per-line decision (22 §11.5).</summary>
public enum ClaimDecisionKind { Approve, PartiallyApprove, Deny, Adjust, RequestInfo, RouteToClinical }

/// <summary>Kind of append-only adjustment (22 §11.5).</summary>
public enum AdjustmentType { PriceCorrection, QuantityCorrection, Deduction, Recovery, Clawback, Writeoff, Reversal, Void, Reallocation }

/// <summary>Batch payee kind (22 §11.5 "Batch type").</summary>
public enum BatchType { Provider, Reimbursement }

/// <summary>How a batch's claims were selected (22 §11.5).</summary>
public enum BatchSelectionMode { DateRange, ProviderBranch, ProviderGroup, Manual }

/// <summary>How a reimbursement request was matched (22 §11.5).</summary>
public enum ReimbursementMatchMethod { AutoOcr, Manual, Unmatched }

/// <summary>Kind of evidence document linked to a claim / reimbursement (22 §11.5).</summary>
public enum ClaimDocType { Invoice, Receipt, ResultProof, DispenseProof, Statement, SettlementAdvice, Other }

/// <summary>Claim lifecycle (23-state-machines §7).</summary>
public enum ClaimStatus { Draft, Submitted, UnderAdjudication, PendingInfo, ClinicalReview, Approved, PartiallyApproved, Denied, Settled, Appealed, Void }

/// <summary>Claim-line lifecycle (23-state-machines §8).</summary>
public enum ClaimLineStatus { Pending, Approved, PartiallyApproved, Denied, Adjusted, Void }

/// <summary>Batch lifecycle (23-state-machines §9).</summary>
public enum BatchStatus { Open, UnderReview, Decided, SettlementIssued, Closed, Cancelled }

/// <summary>Reimbursement lifecycle (23-state-machines §10).</summary>
public enum ReimbursementStatus { Submitted, OcrProcessing, AutoMatched, ManualAssessment, Adjudicating, Approved, PartiallyApproved, Denied, Paid, Void }

/// <summary>Provider-submission header lifecycle (10b.5). Set from the per-line matching outcome.</summary>
public enum SubmissionStatus { Received, Matched, PartiallyMatched, Unmatched }

/// <summary>Reconciliation discrepancy bucket (10b.7, 36 §7). Every discrepancy lands in exactly ONE bucket by the
/// documented precedence: Duplicate &gt; BilledNotDelivered &gt; DeliveredNotBilled &gt; PriceVariance &gt;
/// QuantityVariance &gt; Matched.</summary>
public enum ReconBucket { Matched, BilledNotDelivered, DeliveredNotBilled, PriceVariance, QuantityVariance, Duplicate }

/// <summary>Per-line matching outcome of a provider submission (10b.5).
/// <c>Matched</c> — a delivered/authorized fulfillment was found, a priced payable line was created;
/// <c>Unmatched</c> — no fulfillment record → NO_FULFILLMENT_RECORD, RequiresManualReview, manual queue;
/// <c>Duplicate</c> — a live payable line already exists for that fulfillment → DUPLICATE_CLAIM (no second line).</summary>
public enum SubmissionLineOutcome { Matched, Unmatched, Duplicate }
