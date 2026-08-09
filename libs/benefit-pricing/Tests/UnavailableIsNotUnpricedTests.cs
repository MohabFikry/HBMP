using FluentAssertions;
using Cash = Mersal.Amounts.Money;

namespace Mersal.BenefitPricing.Tests;

/// <summary>
/// A cost share that could not be READ is never reported as a cost share that does not EXIST.
/// </summary>
/// <remarks>
/// <para>
/// The two were one outcome. <see cref="IBenefitCostShareSource"/> returns null, the service mapped null to
/// <see cref="TierPricingFailure.NotPricedAtTier"/>, and every consumer turned that into the sentence "this
/// plan version does not price this benefit category at the resolved tier" — a claim about a member's
/// benefit.
/// </para>
/// <para>
/// It was being said on the strength of a 403. The cost-share route sat behind <c>policy:read</c>, which a
/// pharmacist does not hold and never should (it is the entire benefit product), so the shared pricing path
/// took a refusal on every quote made at a counter and reported it as a fact about the plan. Nobody could see
/// it, because "the plan does not price pharmacy" was ALSO true — no plan version had a pharmacy rule — and a
/// true sentence arrived at by a broken route is indistinguishable from a working one until the data changes.
/// </para>
/// <para>
/// This is the same rule the clinical checks are built on (ADR-0033): a failed fetch is not a finding. It
/// applies to money for the same reason, and the person it protects is the same person.
/// </para>
/// </remarks>
public class UnavailableIsNotUnpricedTests
{
    private static readonly Guid PlanVersion = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000f1");
    private static readonly Guid TierId = Guid.Parse("cccccccc-0000-0000-0000-0000000000f1");
    private static readonly TierQuery Query =
        new(Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000f1"), new DateOnly(2026, 8, 4), null, null);

    private static TierPricingRequest Request() =>
        new(PlanVersion, "PHARMACY", Query, Cash.Egp(290.50m));

    private static readonly ResolvedTier Tier = new(TierId, "T1", false, "Provider");

    [Fact]
    public async Task A_refused_read_is_Unavailable_and_not_NotPricedAtTier()
    {
        // A 403 from policy-service. `EnsureSuccessStatusCode` throws it out of the source, and the whole
        // point is that it must not land in the same bucket as an authored answer.
        var service = new TierPricingService(
            new StubTier(Tier), new ThrowingCostShare(new HttpRequestException("403 Forbidden")));

        var result = await service.PriceAsync(Request(), bearerToken: null);

        result.Pricing.Should().BeNull();
        result.Failure.Should().Be(TierPricingFailure.Unavailable);
        result.Failure.Should().NotBe(TierPricingFailure.NotPricedAtTier);
    }

    [Fact]
    public async Task A_timeout_is_Unavailable_too()
    {
        // Policy-service slow or restarting. "We did not get an answer in time" is not "the answer is no",
        // and a counter quoting from the second would tell a member their medicine is uncovered.
        var service = new TierPricingService(
            new StubTier(Tier), new ThrowingCostShare(new TaskCanceledException()));

        (await service.PriceAsync(Request(), bearerToken: null)).Failure
            .Should().Be(TierPricingFailure.Unavailable);
    }

    [Fact]
    public async Task A_genuine_404_is_still_NotPricedAtTier()
    {
        // The distinction has to cut both ways, or it is just a rename. The source returns null ONLY for a
        // 404, which is policy-service saying this version really does not price this category at this tier —
        // an authored answer, and a plan gap the caller should surface as one.
        var service = new TierPricingService(new StubTier(Tier), new StubCostShare(null));

        (await service.PriceAsync(Request(), bearerToken: null)).Failure
            .Should().Be(TierPricingFailure.NotPricedAtTier);
    }

    [Fact]
    public async Task An_unresolved_tier_keeps_its_own_reason()
    {
        // Three failures, three causes: a network-administration gap, a plan gap, and an outage. Collapsing
        // any two sends whoever has to fix it to the wrong team.
        var service = new TierPricingService(new StubTier(null), new StubCostShare(null));

        (await service.PriceAsync(Request(), bearerToken: null)).Failure
            .Should().Be(TierPricingFailure.TierUnresolved);
    }

    private sealed class StubTier(ResolvedTier? tier) : INetworkTierResolver
    {
        public Task<ResolvedTier?> ResolveAsync(TierQuery query, string? bearerToken, CancellationToken ct = default)
            => Task.FromResult(tier);
    }

    private sealed class StubCostShare(BenefitCostShare? terms) : IBenefitCostShareSource
    {
        public Task<BenefitCostShare?> GetAsync(
            Guid planVersionId, string benefitCategoryCode, Guid networkTierId, string? bearerToken,
            CancellationToken ct = default) => Task.FromResult(terms);
    }

    private sealed class ThrowingCostShare(Exception ex) : IBenefitCostShareSource
    {
        public Task<BenefitCostShare?> GetAsync(
            Guid planVersionId, string benefitCategoryCode, Guid networkTierId, string? bearerToken,
            CancellationToken ct = default) => Task.FromException<BenefitCostShare?>(ex);
    }
}
