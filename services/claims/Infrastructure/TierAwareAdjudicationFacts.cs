using Mersal.BenefitPricing;
using Mersal.Claims.Domain;

namespace Mersal.Claims.Infrastructure;

/// <summary>
/// Phase 19.1b consumption — adjudication step 6 (provider network status) and step 9 (the member/payer split),
/// resolved from the network tier IN FORCE ON THE SERVICE DATE.
///
/// Replaces <see cref="PermissiveAdjudicationFacts"/>, which treated every provider as in-network with a zero
/// co-pay. Two things make this the load-bearing consumer:
///
/// <list type="bullet">
/// <item><b>The tier is resolved at the SERVICE date, not today.</b> A provider moved from out-of-network to T1
/// in March must not change what February's care is adjudicated at — and an already-settled February claim must
/// re-adjudicate to the same numbers if it is ever recomputed.</item>
/// <item><b>The split comes from the same <c>libs/money</c> calculator eligibility previews with.</b> A
/// beneficiary told "you pay 100" at the counter and billed 400 afterwards is the failure the shared path
/// exists to make impossible.</item>
/// </list>
///
/// <para><b>Fail-closed, but not fail-deny.</b> When the tier or its cost share cannot be resolved this does not
/// silently pass the line as in-network with no member share. It marks the line out-of-network, which the
/// adjudicator turns into <c>PROVIDER_OUT_OF_NETWORK</c> — a recommendation a Claims Officer reviews, not a
/// payment. Guessing "in network, zero co-pay" would pay the best negotiated rate to a provider nobody
/// negotiated with, and nothing downstream would question it.</para>
/// </summary>
public sealed class TierAwareAdjudicationFacts(
    TierPricingService pricing, IBenefitCategoryResolver categories, IPlanVersionForClaim planVersions)
    : IExternalAdjudicationFacts
{
    public async Task<ExternalFacts> GetAsync(Claim claim, ClaimLine line, string? bearer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(line);

        var gated = line.AuthorizationId is not null;
        var baseline = new ExternalFacts
        {
            IsGatedService = gated,
            Authorization = gated ? AuthorizationState.Approved : AuthorizationState.None,
        };

        // A claim with no performing provider cannot be tier-resolved at all. That is an intake gap, not an
        // in-network finding — so it stays on the permissive baseline and the officer sees the line unpriced
        // rather than silently paid at the best rate.
        if (claim.ProviderId is not { } providerId) return baseline;

        var category = await categories.ResolveAsync(line.CodeSystem, line.Code, bearer, ct);
        if (category is null) return baseline;

        var planVersionId = await planVersions.ResolveAsync(claim, ct);
        if (planVersionId is not { } versionId) return baseline;

        // THE service date. claim.ServiceDateFrom is the date the care happened; using DateTime.Today here
        // would re-price history every time a claim was recomputed.
        var query = new TierQuery(providerId, claim.ServiceDateFrom, claim.ProviderLocationId, line.Code);

        // Price against the CONTRACT price where there is one — the allowed amount the split applies to is
        // what the payer agreed, not what the provider billed.
        var allowed = Mersal.Amounts.Money.Egp(line.ContractPrice ?? line.BilledAmount);
        var result = await pricing.PriceAsync(
            new TierPricingRequest(versionId, category, query, allowed), bearer, ct);

        if (result.Pricing is not { } quote)
        {
            // Unresolvable → out-of-network for the officer to look at. See the fail-closed note above.
            return baseline with { ProviderInNetwork = false };
        }

        return baseline with
        {
            // Step 6. "Not covered at this tier" is an out-of-network finding in adjudication's vocabulary:
            // the plan pays nothing for care delivered there.
            ProviderInNetwork = quote.Terms.IsCovered && !quote.Tier.IsOutOfNetwork,
            // Step 3. The tier can gate a service that is open-access in-network.
            IsGatedService = gated || quote.RequiresPreauth,
            Authorization = gated ? AuthorizationState.Approved : baseline.Authorization,
            // Step 9. The SAME split eligibility previewed.
            MemberShare = quote.Split.MemberShare.Amount,
        };
    }
}

/// <summary>Maps a claim line's coded service to the benefit category the plan prices. Kept a seam because the
/// mapping is master-data owned (CPT/LOINC → category) and belongs to masterdata-service, not here.</summary>
public interface IBenefitCategoryResolver
{
    Task<string?> ResolveAsync(ClaimCodeSystem system, string code, string? bearer, CancellationToken ct = default);
}

/// <summary>The plan version a claim's member was enrolled under. A seam because the member → policy_plan →
/// plan_version link lands in 19.2b; until it does, returning null leaves the line on the permissive baseline
/// rather than adjudicating against a version nobody established.</summary>
public interface IPlanVersionForClaim
{
    Task<Guid?> ResolveAsync(Claim claim, CancellationToken ct = default);
}

/// <summary>Default until 19.2b: no member → plan-version link exists yet, so no tier pricing is attempted.
/// Deliberately null rather than "the current active version" — adjudicating against the version that happens
/// to be current is the exact bug the whole effective-dated layer exists to prevent.</summary>
public sealed class UnresolvedPlanVersionForClaim : IPlanVersionForClaim
{
    public Task<Guid?> ResolveAsync(Claim claim, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
}

/// <summary>Default: the platform's five seeded categories keyed off the code system, which is the coarse
/// mapping the seeded master data supports today.</summary>
public sealed class CodeSystemBenefitCategoryResolver : IBenefitCategoryResolver
{
    public Task<string?> ResolveAsync(ClaimCodeSystem system, string code, string? bearer, CancellationToken ct = default) =>
        Task.FromResult<string?>(system switch
        {
            ClaimCodeSystem.LOINC => "LAB",
            ClaimCodeSystem.DRUG => "PHARMACY",
            _ => null,   // CPT covers imaging, consults and procedures — too coarse to guess from the system alone
        });
}
