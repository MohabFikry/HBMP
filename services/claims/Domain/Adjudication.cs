namespace Mersal.Claims.Domain;

/// <summary>Authorization state as it bears on a claim line (36 §5 step 3). A gated service is payable only against a
/// valid, non-expired authorization; a <see cref="PartiallyApproved"/> scope CAPS the payable line.</summary>
public enum AuthorizationState { None, Approved, PartiallyApproved, EmergencyApproved, Overridden, Expired }

/// <summary>The min-necessary, clinical-free fact bag the adjudicator needs for ONE line — gathered at the boundary
/// from eligibility/policy/approvals/provider + the claim line itself. Carries codes, amounts, statuses and booleans
/// only; never a diagnosis. Coverage-limit facts are READ (limit − consumed); the claims path never writes them.</summary>
public sealed record AdjudicationFacts
{
    public required decimal BilledAmount { get; init; }
    public decimal? ContractPrice { get; init; }            // null ⇒ NO_TARIFF (manual pricing)

    public bool BeneficiaryEligible { get; init; } = true;   // step 1
    public bool PolicyValid { get; init; } = true;           // step 1
    public bool CoverageCategoryMatches { get; init; } = true; // step 2

    public bool IsGatedService { get; init; }                 // step 3
    public AuthorizationState Authorization { get; init; } = AuthorizationState.None;
    public decimal? AuthorizedScopeAmount { get; init; }      // cap when PartiallyApproved

    public bool HasFulfillmentRecord { get; init; } = true;   // step 4
    public bool IsDuplicate { get; init; }                    // step 5

    public bool ProviderInNetwork { get; init; } = true;      // step 6
    public bool ContractEffective { get; init; } = true;      // step 6

    public decimal? LimitRemaining { get; init; }             // step 8; null ⇒ unlimited (READ-ONLY)
    public decimal MemberShare { get; init; }                 // step 9 co-pay / deductible
}

/// <summary>The output of pre-adjudication for one line (persisted on the line; the append-only per-run history lives
/// in <c>audit_event</c>, since doc 22 defines no adjudication-run table).</summary>
public sealed record AdjudicationResult(
    SystemRecommendation Recommendation,
    IReadOnlyList<string> ReasonCodes,
    decimal? AllowedAmount,
    decimal MemberShare,
    string RuleVersion);

/// <summary>The automated pre-adjudication rules engine (36 §5). Runs the fixed 9-step order per line and COLLECTS
/// ALL applicable reason codes (never stops at the first failure), so partial approvals are precise. The system
/// RECOMMENDS; the Claims Officer decides (10b.4). Pure + deterministic → fully table-test-driven, no I/O. NEVER
/// invents a price (NO_TARIFF ⇒ manual review, allowed stays null) and NEVER writes a coverage accumulator.</summary>
public static class Adjudicator
{
    public const string RuleVersion = "10b.3.0";

    /// <summary>Reason codes that block payment outright — any one ⇒ RecommendDeny (allowed 0).</summary>
    private static readonly HashSet<string> HardBlocks = new(StringComparer.Ordinal)
    {
        ReasonCodes.NotEligible, ReasonCodes.PolicyExpired, ReasonCodes.NotCoveredCategory,
        ReasonCodes.NoPriorAuth, ReasonCodes.AuthExpired, ReasonCodes.NoFulfillmentRecord,
        ReasonCodes.DuplicateClaim, ReasonCodes.ProviderOutOfNetwork, ReasonCodes.ContractNotEffective,
    };

    /// <summary>Reason codes that CAP (not block) — a line with only these is a precise partial approval.</summary>
    private static readonly HashSet<string> Caps = new(StringComparer.Ordinal)
    {
        ReasonCodes.ExceedsAuthScope, ReasonCodes.LimitExceeded,
    };

    public static AdjudicationResult Evaluate(AdjudicationFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var reasons = new List<string>();

        // 1. Beneficiary status + policy validity on the service date.
        if (!f.BeneficiaryEligible) reasons.Add(ReasonCodes.NotEligible);
        if (!f.PolicyValid) reasons.Add(ReasonCodes.PolicyExpired);
        // 2. Coverage category.
        if (!f.CoverageCategoryMatches) reasons.Add(ReasonCodes.NotCoveredCategory);
        // 3. Pre-auth linkage (gated services only).
        if (f.IsGatedService)
        {
            switch (f.Authorization)
            {
                case AuthorizationState.None: reasons.Add(ReasonCodes.NoPriorAuth); break;
                case AuthorizationState.Expired: reasons.Add(ReasonCodes.AuthExpired); break;
            }
        }
        // 4. Fulfillment linkage.
        if (!f.HasFulfillmentRecord) reasons.Add(ReasonCodes.NoFulfillmentRecord);
        // 5. Duplicate check.
        if (f.IsDuplicate) reasons.Add(ReasonCodes.DuplicateClaim);
        // 6. Provider network status.
        if (!f.ProviderInNetwork) reasons.Add(ReasonCodes.ProviderOutOfNetwork);
        if (!f.ContractEffective) reasons.Add(ReasonCodes.ContractNotEffective);
        // 7. Tariff pricing.
        if (f.ContractPrice is null) reasons.Add(ReasonCodes.NoTariff);

        // Base payable (before caps): the lesser of billed vs contract price (never a guessed price).
        decimal? basePayable = f.ContractPrice is null ? null : Math.Min(f.BilledAmount, f.ContractPrice.Value);
        var cap = basePayable;

        // 3b. PartiallyApproved authorization caps the line.
        if (f.IsGatedService && f.Authorization == AuthorizationState.PartiallyApproved
            && f.AuthorizedScopeAmount is { } scope && cap is { } c0 && scope < c0)
        {
            reasons.Add(ReasonCodes.ExceedsAuthScope);
            cap = scope;
        }
        // 8. Coverage limit availability (READ ONLY — the claims path never decrements consumed_value).
        if (f.LimitRemaining is { } rem && cap is { } c1 && rem < c1)
        {
            reasons.Add(ReasonCodes.LimitExceeded);
            cap = Math.Max(0m, rem);
        }

        // 9. Co-pay / deductible split → member vs payer share.
        decimal? allowed = cap is null ? null : Math.Max(0m, cap.Value - f.MemberShare);

        var recommendation = Decide(reasons, cappedBelowBase: cap < basePayable);
        if (recommendation == SystemRecommendation.RecommendDeny) allowed = 0m;
        if (recommendation == SystemRecommendation.RequiresManualReview) allowed = null;

        return new AdjudicationResult(recommendation, reasons, allowed, f.MemberShare, RuleVersion);
    }

    private static SystemRecommendation Decide(IReadOnlyCollection<string> reasons, bool cappedBelowBase)
    {
        if (reasons.Any(HardBlocks.Contains)) return SystemRecommendation.RecommendDeny;
        if (reasons.Contains(ReasonCodes.NoTariff)) return SystemRecommendation.RequiresManualReview;
        if (cappedBelowBase || reasons.Any(Caps.Contains)) return SystemRecommendation.RecommendPartial;
        return SystemRecommendation.RecommendApprove;
    }
}
