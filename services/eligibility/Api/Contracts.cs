using Mersal.Eligibility.Domain;

namespace Mersal.Eligibility.Api;

/// <summary>POST /eligibility/check request (17-api-specifications §5).</summary>
public sealed record EligibilityCheckRequest(
    Guid BeneficiaryId,
    string BenefitCategory,
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
    CostSharePreviewResponse? CostShare = null)
{
    public static EligibilityCheckResponse From(
        EligibilityResult r, DateTimeOffset expires, bool fromCache, CostSharePreviewResponse? costShare = null) => new(
        r.Decision.ToString(),
        r.CoverageId,
        r.Reasons,
        r.LimitState is null ? null
            : new LimitStateResponse(r.LimitState.LimitType.ToString(), r.LimitState.LimitValue, r.LimitState.ConsumedValue, r.LimitState.Remaining),
        expires,
        fromCache,
        costShare);
}
