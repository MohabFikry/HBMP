namespace Mersal.Claims.Domain;

/// <summary>A claim — a beneficiary's delivered, authorized services rolled up for one payee/period (22 §10A.1).
/// Submitted claims are NEVER mutated or hard-deleted: corrections are <c>claim_adjustment</c> rows or a
/// compensating Void + re-claim. The schema carries NO clinical column anywhere — codes + amounts only.</summary>
public sealed class Claim
{
    public Guid ClaimId { get; set; }
    public string ClaimNo { get; set; } = default!;
    public ClaimOrigin Origin { get; set; }
    public Guid BeneficiaryId { get; set; }
    /// <summary>Payee provider — null only for reimbursement claims.</summary>
    public Guid? ProviderId { get; set; }
    public Guid? ProviderLocationId { get; set; }
    public Guid? BatchId { get; set; }
    public Guid? AuthorizationId { get; set; }
    public DateOnly ServiceDateFrom { get; set; }
    public DateOnly? ServiceDateTo { get; set; }
    public string CurrencyCode { get; set; } = "EGP";
    public decimal ClaimedAmount { get; set; }
    public decimal? PricedAmount { get; set; }
    public decimal? ApprovedAmount { get; set; }
    public decimal? AdjustedAmount { get; set; }
    public decimal? NetPayable { get; set; }
    public ClaimStatus Status { get; set; } = ClaimStatus.Draft;
    /// <summary>Tenant the claim belongs to (isolation + RLS). Not shown in 22's column list but required for
    /// multi-tenant row scoping consistently with the rest of the platform.</summary>
    public string TenantId { get; set; } = default!;
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Actor who originated the claim (SoD: the decider may never be this principal).</summary>
    public string? CreatedBy { get; set; }
    public uint RowVersion { get; set; }

    public List<ClaimLine> Lines { get; set; } = [];
}

/// <summary>A payable line anchored to exactly one fulfillment/dispense record (22 §10A.2). The partial unique index
/// <c>UNIQUE(fulfillment_ref) WHERE fulfillment_ref IS NOT NULL AND status &lt;&gt; 'Void'</c> makes double-billing
/// impossible at the database — a second live line for the same reference fails and surfaces as DUPLICATE_CLAIM.</summary>
public sealed class ClaimLine
{
    public Guid ClaimLineId { get; set; }
    public Guid ClaimId { get; set; }
    public Guid? FulfillmentRef { get; set; }
    public FulfillmentType FulfillmentType { get; set; } = FulfillmentType.None;
    public ClaimCodeSystem CodeSystem { get; set; }
    public string Code { get; set; } = default!;
    public string? Description { get; set; }
    public decimal Quantity { get; set; }
    public decimal BilledAmount { get; set; }
    /// <summary>Contract tariff on the service date; null ⇒ NO_TARIFF ⇒ manual pricing (never a guessed price).</summary>
    public decimal? ContractPrice { get; set; }
    public decimal? AllowedAmount { get; set; }
    public decimal? MemberShare { get; set; }
    public ClaimLineStatus Status { get; set; } = ClaimLineStatus.Pending;
    public SystemRecommendation? SystemRecommendation { get; set; }
    /// <summary>Coded reasons recorded on the line at intake/adjudication (all applicable, never just the first).</summary>
    public List<string> ReasonCodes { get; set; } = [];
    public string? RuleVersion { get; set; }
    /// <summary>The authorization this line is billed against (mandatory for gated services; checked at adjudication).</summary>
    public Guid? AuthorizationId { get; set; }
    public uint RowVersion { get; set; }
}

/// <summary>Idempotency ledger for consumed domain events — a redelivered event id is a no-op (dedupe on id).</summary>
public sealed class ProcessedEvent
{
    public Guid EventId { get; set; }
    public string EventType { get; set; } = default!;
    public DateTimeOffset ConsumedAt { get; set; }
}
