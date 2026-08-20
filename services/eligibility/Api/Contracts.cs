using Mersal.Eligibility.Domain;

namespace Mersal.Eligibility.Api;

/// <summary>POST /eligibility/check request (17-api-specifications §5).</summary>
public sealed record EligibilityCheckRequest(
    Guid BeneficiaryId,
    /// <summary>
    /// The benefit category the care falls under — LAB, IMAGING, CONSULTATION and so on.
    ///
    /// <para>32.6 — OPTIONAL, because a receptionist with a walk-in in front of them does not always know it
    /// yet, and the alternative was worse than asking without one: the desk computed a verdict in the browser
    /// from a cached member status and never called this endpoint at all. The category-less answer is about
    /// the MEMBERSHIP and says so (<see cref="EligibilityCheckResponse.DecisionScope"/>); it is not a coverage
    /// verdict wearing one's clothes, and it carries no cost share.</para>
    /// </summary>
    string? BenefitCategory,
    string? ServiceCode,
    bool? ServiceRequiresPreAuth,
    // 19.1b — where and when the care happens. Optional: a provider-independent check is still a legitimate
    // question ("is this member covered for Lab at all?"), just a different one, and it is cached as one.
    /// <summary>The plan version the member's entitlement was generated from. Until enrolment links member →
    /// policy_plan → plan_version (19.2b), the caller supplies it; without it no cost share is quoted, which is
    /// the honest answer rather than a guessed one.</summary>
    Guid? PlanVersionId = null,
    Guid? ProviderId = null,
    Guid? LocationId = null,
    DateOnly? ServiceDate = null,
    /// <summary>Estimated allowed amount for the cost-share preview. Without it the tier and its terms are
    /// still reported — the member is told the RULE ("40% out-of-network") rather than an amount, which is
    /// honest, where inventing an amount from a guessed price would not be.</summary>
    decimal? EstimatedAmount = null);

/// <summary>Denormalized limit state for the response.</summary>
public sealed record LimitStateResponse(string LimitType, decimal LimitValue, decimal ConsumedValue, decimal Remaining);

/// <summary>
/// 19.1b — what the member will pay at the tier resolved for this provider and service date.
///
/// This is the number a receptionist reads out to a beneficiary standing in front of them, with no reviewer in
/// the loop and no recovery path if it is wrong. It is therefore computed by the SAME
/// <c>libs/money</c> split that claims adjudicates with, reached through the same
/// <c>libs/benefit-pricing</c> composition — not by a second implementation that happens to agree today.
/// </summary>
/// <param name="Determinate">False when the tier or its cost share could not be resolved. The card must then
/// say so rather than show a zero, which would read as "free".</param>
public sealed record CostSharePreviewResponse(
    string? TierCode,
    string? TierBasis,
    bool IsCoveredAtTier,
    bool RequiresPreauthAtTier,
    bool Determinate,
    string? Reason,
    decimal? EstimatedAllowedAmount,
    decimal? EstimatedMemberShare,
    decimal? EstimatedPayerShare,
    decimal? Deductible,
    bool DeductibleWaived,
    decimal? CopayFixed,
    decimal? CopayPercent,
    decimal? CoinsurancePercent);

/// <summary>POST /eligibility/check response (17-api-specifications §5).</summary>
public sealed record EligibilityCheckResponse(
    string Decision,
    Guid? CoverageId,
    IReadOnlyList<string> Reasons,
    LimitStateResponse? LimitState,
    DateTimeOffset SnapshotExpiresAt,
    bool FromCache,
    CostSharePreviewResponse? CostShare = null,
    /// <summary>
    /// WHAT was decided: <c>Benefit</c> when a category was named, <c>Membership</c> when none was.
    ///
    /// <para>32.6 — on the wire rather than inferred, because the two answers look identical and mean
    /// different things. "Eligible" at membership scope says this person is an active member in good
    /// standing; it says NOTHING about whether a particular service is covered, and a desk that reads it as
    /// the second has been told a beneficiary is covered for care nobody checked. A consumer that ignores
    /// this field is wrong in a way it cannot detect, which is why it is not optional.</para>
    /// </summary>
    string DecisionScope = EligibilityDecisionScope.Benefit)
{
    public static EligibilityCheckResponse From(
        EligibilityResult r, DateTimeOffset expires, bool fromCache, CostSharePreviewResponse? costShare = null,
        string scope = EligibilityDecisionScope.Benefit) => new(
        r.Decision.ToString(),
        r.CoverageId,
        r.Reasons,
        r.LimitState is null ? null
            : new LimitStateResponse(r.LimitState.LimitType.ToString(), r.LimitState.LimitValue, r.LimitState.ConsumedValue, r.LimitState.Remaining),
        expires,
        fromCache,
        costShare,
        scope);
}

/// <summary>32.6 — the two scopes a check can answer at. Strings rather than an enum on the wire, matching
/// how <c>Decision</c> is already carried.</summary>
public static class EligibilityDecisionScope
{
    /// <summary>A named benefit category was checked: cover, limits and cost share all apply to it.</summary>
    public const string Benefit = "Benefit";

    /// <summary>No category was named. The answer is about the member's standing and nothing else.</summary>
    public const string Membership = "Membership";
}
