using Mersal.Money;

namespace Mersal.BenefitPricing;

// Phase 19.1b consumption — the ONE path from (provider, service date, benefit category) to what the member
// pays. approvals, eligibility and claims all go through here.
//
// WHY THIS IS A SHARED LIBRARY AND NOT THREE IMPLEMENTATIONS. The amount a receptionist reads off an
// eligibility card and the amount a claim finally charges must be the same number. They are produced by
// different services, at different times, for different audiences — which is exactly the situation where two
// implementations drift and nobody notices until a beneficiary is told one figure and billed another. A
// refugee at a counter has no reviewer in the loop and no recovery path; a claim at least passes through
// officer review, settlement advice and adjustment. So the composition lives once, here, and the arithmetic
// lives once, in libs/money.

/// <summary>Which provider/location/service, and on what date. The date is REQUIRED and is the SERVICE date,
/// never today: a provider moving tier in March must not change what February's care was priced at.</summary>
public readonly record struct TierQuery(Guid ProviderId, DateOnly ServiceDate, Guid? LocationId = null, string? ServiceCode = null);

/// <summary>The tier in force, as provider-service resolved it. <see cref="Basis"/> distinguishes
/// "assigned to the out-of-network tier" from "nothing was assigned, so out-of-network was the safe default" —
/// same price, very different follow-up.</summary>
public sealed record ResolvedTier(Guid NetworkTierId, string TierCode, bool IsOutOfNetwork, string Basis)
{
    /// <summary>True when no assignment matched and the fail-safe default was applied.</summary>
    public bool IsFallback => string.Equals(Basis, "DefaultOutOfNetwork", StringComparison.Ordinal);
}

/// <summary>The authored cost share for one (plan version, benefit category, tier), straight from
/// policy.benefit_rule + benefit_rule_tier. A record of what was AGREED — no arithmetic lives here.</summary>
public sealed record BenefitCostShare(
    Guid NetworkTierId,
    string TierCode,
    bool IsCovered,
    decimal? CopayFixed,
    decimal? CopayPercent,
    decimal? CoinsurancePercent,
    decimal? Deductible,
    bool DeductibleWaived,
    bool CopayCountsTowardDeductible,
    bool RequiresPreauth,
    decimal? LimitValue)
{
    /// <summary>The single translation into the money kernel's vocabulary. Having exactly one of these is what
    /// makes the parity between eligibility and claims structural rather than coincidental.</summary>
    public TierCostShareTerms ToTerms() => new(
        IsCovered, CopayFixed, CopayPercent, CoinsurancePercent, Deductible,
        DeductibleWaived, CopayCountsTowardDeductible);
}

/// <summary>provider-service's tier resolver (<c>GET /api/v1/network-tiers/resolve</c>).</summary>
public interface INetworkTierResolver
{
    /// <returns>null when the provider is unknown or no out-of-network tier is configured to fall back to —
    /// a network-administration gap the caller must surface, never paper over with a guess.</returns>
    Task<ResolvedTier?> ResolveAsync(TierQuery query, string? bearerToken, CancellationToken ct = default);
}

/// <summary>policy-service's authored cost share (<c>GET /api/v1/plan-versions/{id}/cost-share</c>).</summary>
public interface IBenefitCostShareSource
{
    /// <returns>null when the version does not price this category at this tier. NOT an error to swallow:
    /// activation refuses to leave an Active tier unpriced, so a null here means the caller is asking about a
    /// tier that did not exist when the version was authored.</returns>
    Task<BenefitCostShare?> GetAsync(
        Guid planVersionId, string benefitCategoryCode, Guid networkTierId, string? bearerToken, CancellationToken ct = default);
}

/// <summary>What to price.</summary>
public sealed record TierPricingRequest(
    Guid PlanVersionId, string BenefitCategoryCode, TierQuery Tier, Money.Money AllowedAmount);

/// <summary>The priced answer: which tier applied, what was agreed there, the split, and whether
/// pre-authorization is required — the tier's override having already been resolved.</summary>
public sealed record TierPricing(
    ResolvedTier Tier, BenefitCostShare Terms, CostShareSplit Split, bool RequiresPreauth);

/// <summary>Why pricing could not be produced. Distinguished rather than collapsed to null, because each one
/// needs a different response: a missing tier is a network gap, a missing price is a plan gap, and telling a
/// beneficiary "covered" in either case would be a guess.</summary>
public enum TierPricingFailure
{
    None,
    /// <summary>The provider resolves to no tier — a network-administration gap.</summary>
    TierUnresolved,
    /// <summary>The version genuinely does not price this category at this tier — a plan gap.</summary>
    NotPricedAtTier,
    /// <summary>
    /// The question could not be ASKED: policy-service refused, failed or did not answer.
    /// </summary>
    /// <remarks>
    /// Distinguished from <see cref="NotPricedAtTier"/> because the two are opposite kinds of statement and
    /// were being collapsed into one. The cost-share route was gated on <c>policy:read</c>, which a
    /// pharmacist does not hold, so every quote at a counter took a 403 — and the caller reported it as "the
    /// plan does not price pharmacy at this tier", which is a claim about the member's benefit made on the
    /// strength of a permission error. A failed read is never a finding; that is the same rule the clinical
    /// checks follow (ADR-0033) and it applies to money for the same reason.
    /// </remarks>
    Unavailable,
}

/// <summary>The result, carrying the reason when there is no pricing.</summary>
public readonly record struct TierPricingResult(TierPricing? Pricing, TierPricingFailure Failure)
{
    public bool Succeeded => Pricing is not null;
    public static TierPricingResult Ok(TierPricing p) => new(p, TierPricingFailure.None);
    public static TierPricingResult Failed(TierPricingFailure why) => new(null, why);
}

/// <summary>Whether authorization is required, and whether we actually know. <c>Determinate</c> is part of the
/// answer rather than an implementation detail: "the plan says no authorization is needed" and "we could not
/// tell, so we are requiring one" call for different follow-up, and a caller that cannot distinguish them will
/// read a resolution outage as a benefit decision.</summary>
public readonly record struct PreauthDetermination(
    bool Required, ResolvedTier? Tier, bool Determinate, TierPricingFailure Failure)
{
    /// <summary>Unknown, therefore required — see the fail-closed note on the caller.</summary>
    public static PreauthDetermination Indeterminate(ResolvedTier? tier, TierPricingFailure why) =>
        new(Required: true, tier, Determinate: false, why);
}

/// <summary>
/// The composition every consumer shares: resolve the tier in force on the service date, look up what the plan
/// version agreed at that tier, and split the amount with the money kernel.
/// </summary>
public sealed class TierPricingService(INetworkTierResolver tiers, IBenefitCostShareSource costShare)
{
    public async Task<TierPricingResult> PriceAsync(
        TierPricingRequest request, string? bearerToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tier = await tiers.ResolveAsync(request.Tier, bearerToken, ct);
        if (tier is null) return TierPricingResult.Failed(TierPricingFailure.TierUnresolved);

        BenefitCostShare? terms;
        try
        {
            terms = await costShare.GetAsync(
                request.PlanVersionId, request.BenefitCategoryCode, tier.NetworkTierId, bearerToken, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // "We could not ask" is not "the answer is no". The source returns null ONLY for a 404, which is
            // the authored answer that this tier is unpriced; everything else lands here and is reported as
            // unavailable so the caller can say so instead of inventing a benefit fact.
            return TierPricingResult.Failed(TierPricingFailure.Unavailable);
        }

        if (terms is null) return TierPricingResult.Failed(TierPricingFailure.NotPricedAtTier);

        var split = CostShareCalculator.Split(request.AllowedAmount, terms.ToTerms());
        return TierPricingResult.Ok(new TierPricing(tier, terms, split, terms.RequiresPreauth));
    }

    /// <summary>
    /// Pre-authorization alone, for approvals — which needs the tier's override but has no amount to split yet.
    /// Deliberately the SAME resolution path, so an approval and the claim that follows it cannot disagree
    /// about which tier the care was delivered at.
    ///
    /// <para>FAILS CLOSED. When the tier or its cost share cannot be resolved, the answer is <c>Required</c>
    /// with <c>Determinate = false</c>: the safe reading of "we cannot tell whether this needed authorization"
    /// is that it did. Answering "not required" would let an unpriced service through the one gate designed to
    /// catch it.</para>
    /// </summary>
    public async Task<PreauthDetermination> RequiresPreauthAsync(
        Guid planVersionId, string benefitCategoryCode, TierQuery query, string? bearerToken, CancellationToken ct = default)
    {
        var tier = await tiers.ResolveAsync(query, bearerToken, ct);
        if (tier is null) return PreauthDetermination.Indeterminate(null, TierPricingFailure.TierUnresolved);

        var terms = await costShare.GetAsync(planVersionId, benefitCategoryCode, tier.NetworkTierId, bearerToken, ct);
        return terms is null
            ? PreauthDetermination.Indeterminate(tier, TierPricingFailure.NotPricedAtTier)
            : new PreauthDetermination(terms.RequiresPreauth, tier, Determinate: true, TierPricingFailure.None);
    }
}
