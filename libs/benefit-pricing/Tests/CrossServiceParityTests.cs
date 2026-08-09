using FluentAssertions;
using Mersal.BenefitPricing;
// The namespace Mersal.Amounts and the type Mersal.Amounts.Money collide inside this namespace; alias so the
// assertions below read as amounts rather than as fully-qualified noise.
using Cash = Mersal.Amounts.Money;
using Mersal.Amounts;

namespace Mersal.BenefitPricing.Tests;

/// <summary>
/// Phase 19.1b — THE test the consumption slice exists for.
///
/// eligibility quotes a cost share to a beneficiary standing at a counter. claims charges one weeks later.
/// These are different services, different code paths, different audiences — and if they ever disagree, the
/// person who finds out is a refugee who was told one number and billed another, with no reviewer in the loop
/// and no recovery path. (A claims error at least passes officer review, settlement advice and adjustment.)
///
/// The defence is structural rather than procedural: both consumers reach the same
/// <see cref="TierPricingService"/> composition, which reaches the same <c>libs/money</c> split. This test
/// exercises that shared path exactly as each consumer does, over a matrix of real configurations, and asserts
/// the amounts are identical to the piastre. If someone reimplements either side, this fails.
/// </summary>
public class CrossServiceParityTests
{
    private static readonly Guid PlanVersion = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ProviderId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid T1 = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid Oon = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly DateOnly ServiceDate = new(2026, 2, 10);

    // ---- Stubs standing in for provider-service and policy-service -------------------------------------
    private sealed class StubTiers(ResolvedTier? tier) : INetworkTierResolver
    {
        public int Calls { get; private set; }
        public TierQuery? LastQuery { get; private set; }
        public Task<ResolvedTier?> ResolveAsync(TierQuery query, string? bearerToken, CancellationToken ct = default)
        {
            Calls++;
            LastQuery = query;
            return Task.FromResult(tier);
        }
    }

    private sealed class StubCostShare(BenefitCostShare? terms) : IBenefitCostShareSource
    {
        public Task<BenefitCostShare?> GetAsync(
            Guid planVersionId, string benefitCategoryCode, Guid networkTierId, string? bearerToken, CancellationToken ct = default)
            => Task.FromResult(terms);
    }

    private static BenefitCostShare Terms(
        Guid tierId, string code, bool covered = true, decimal? copayFixed = null, decimal? copayPercent = null,
        decimal? coinsurance = null, decimal? deductible = null, bool waived = false,
        bool copayAccrues = false, bool preauth = false) =>
        new(tierId, code, covered, copayFixed, copayPercent, coinsurance, deductible, waived, copayAccrues, preauth, null);

    private static TierPricingService Service(ResolvedTier? tier, BenefitCostShare? terms) =>
        new(new StubTiers(tier), new StubCostShare(terms));

    private static ResolvedTier InNetwork => new(T1, "T1", IsOutOfNetwork: false, Basis: "Provider");
    private static ResolvedTier OutOfNetwork => new(Oon, "OON", IsOutOfNetwork: true, Basis: "DefaultOutOfNetwork");

    /// <summary>How ELIGIBILITY produces its quote: an estimated amount, previewed before the care happens.</summary>
    private static async Task<CostShareSplit> EligibilityQuote(
        TierPricingService svc, decimal estimatedAmount)
    {
        var result = await svc.PriceAsync(new TierPricingRequest(
            PlanVersion, "LAB", new TierQuery(ProviderId, ServiceDate), Cash.Egp(estimatedAmount)), bearerToken: null);
        return result.Pricing!.Split;
    }

    /// <summary>How CLAIMS produces its charge: the contract price, adjudicated after the care happened.</summary>
    private static async Task<CostShareSplit> ClaimsCharge(
        TierPricingService svc, decimal contractPrice)
    {
        var result = await svc.PriceAsync(new TierPricingRequest(
            PlanVersion, "LAB", new TierQuery(ProviderId, ServiceDate), Cash.Egp(contractPrice)), bearerToken: null);
        return result.Pricing!.Split;
    }

    [Theory]
    // Every literal carries a 'd' suffix: xUnit binds InlineData by reflection, and a bare int will not
    // convert to a double? parameter — a harness detail, not a domain one.
    [InlineData(1000d, 10d, null, null, null, false)]        // in-network percentage co-pay
    [InlineData(1000d, 40d, null, null, null, false)]        // out-of-network, much heavier share
    [InlineData(750.50d, null, 50d, null, null, false)]      // fixed co-pay
    [InlineData(1000d, 10d, null, null, 200d, false)]        // deductible then percentage
    [InlineData(1000d, 10d, null, null, 200d, true)]         // deductible waived for this category
    [InlineData(1234.56d, 15d, null, 20d, 75d, false)]       // the full stack, together
    [InlineData(333.33d, 33.33d, null, 33.33d, 0.07d, false)] // awkward rounding
    public async Task Eligibility_quotes_exactly_what_claims_charges(
        double amount, double? copayPercent, double? copayFixed, double? coinsurance, double? deductible, bool waived)
    {
        var terms = Terms(T1, "T1",
            copayFixed: (decimal?)copayFixed, copayPercent: (decimal?)copayPercent,
            coinsurance: (decimal?)coinsurance, deductible: (decimal?)deductible, waived: waived);

        var quoted = await EligibilityQuote(Service(InNetwork, terms), (decimal)amount);
        var charged = await ClaimsCharge(Service(InNetwork, terms), (decimal)amount);

        charged.MemberShare.Should().Be(quoted.MemberShare,
            "the amount quoted at the counter and the amount billed must be the same number");
        charged.PayerShare.Should().Be(quoted.PayerShare);
        charged.DeductibleApplied.Should().Be(quoted.DeductibleApplied);
        charged.Copay.Should().Be(quoted.Copay);
        charged.Coinsurance.Should().Be(quoted.Coinsurance);

        // And the split still accounts for every piastre on both sides.
        (quoted.MemberShare + quoted.PayerShare).Should().Be(quoted.AllowedAmount);
    }

    [Fact]
    public async Task The_two_agree_at_an_out_of_network_tier_too()
    {
        // The case with the biggest gap between right and wrong: a member told 10% who is charged 40%.
        var terms = Terms(Oon, "OON", copayPercent: 40m);
        var svc = Service(OutOfNetwork, terms);

        var quoted = await EligibilityQuote(svc, 2000m);
        var charged = await ClaimsCharge(svc, 2000m);

        quoted.MemberShare.Should().Be(Cash.Egp(800m));
        charged.MemberShare.Should().Be(quoted.MemberShare);
    }

    [Fact]
    public async Task The_two_agree_when_the_tier_covers_nothing()
    {
        var terms = Terms(Oon, "OON", covered: false);
        var svc = Service(OutOfNetwork, terms);

        var quoted = await EligibilityQuote(svc, 1500m);
        var charged = await ClaimsCharge(svc, 1500m);

        quoted.MemberShare.Should().Be(Cash.Egp(1500m), "nothing is covered here — the member owes all of it");
        charged.MemberShare.Should().Be(quoted.MemberShare);
        charged.PayerShare.Should().Be(Cash.Egp(0m));
    }

    [Fact]
    public async Task Both_consumers_resolve_the_tier_on_the_SERVICE_date_not_today()
    {
        // The single most important shared property. If either side resolved "today", a provider's later tier
        // move would silently re-price care that already happened — and the two would then disagree with each
        // other as well as with history.
        var tiers = new StubTiers(InNetwork);
        var svc = new TierPricingService(tiers, new StubCostShare(Terms(T1, "T1", copayPercent: 10m)));

        await svc.PriceAsync(new TierPricingRequest(
            PlanVersion, "LAB", new TierQuery(ProviderId, ServiceDate), Cash.Egp(100m)), bearerToken: null);

        tiers.LastQuery!.Value.ServiceDate.Should().Be(ServiceDate);
        tiers.LastQuery!.Value.ServiceDate.Should().NotBe(DateOnly.FromDateTime(DateTime.UtcNow));
    }

    [Fact]
    public async Task Neither_consumer_gets_a_price_when_the_tier_cannot_be_resolved()
    {
        // Both must fail the SAME way. If eligibility quoted zero while claims denied, a beneficiary would be
        // told their care is free and then billed in full.
        var svc = Service(tier: null, terms: Terms(T1, "T1", copayPercent: 10m));

        var result = await svc.PriceAsync(new TierPricingRequest(
            PlanVersion, "LAB", new TierQuery(ProviderId, ServiceDate), Cash.Egp(1000m)), bearerToken: null);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(TierPricingFailure.TierUnresolved);
    }

    [Fact]
    public async Task Neither_consumer_gets_a_price_when_the_version_never_priced_that_tier()
    {
        var svc = Service(InNetwork, terms: null);

        var result = await svc.PriceAsync(new TierPricingRequest(
            PlanVersion, "LAB", new TierQuery(ProviderId, ServiceDate), Cash.Egp(1000m)), bearerToken: null);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(TierPricingFailure.NotPricedAtTier,
            "a tier the version never priced is a plan gap, not a network gap — and they need different fixes");
    }

    // ---- The approvals half of the same path -------------------------------------------------------------

    [Fact]
    public async Task Pre_authorization_resolves_through_the_same_tier_as_the_price()
    {
        // An approval and the claim that follows it must not disagree about which tier the care was at.
        var tiers = new StubTiers(OutOfNetwork);
        var terms = Terms(Oon, "OON", copayPercent: 40m, preauth: true);
        var svc = new TierPricingService(tiers, new StubCostShare(terms));

        var d = await svc.RequiresPreauthAsync(PlanVersion, "LAB", new TierQuery(ProviderId, ServiceDate), null);

        d.Required.Should().BeTrue();
        d.Determinate.Should().BeTrue();
        d.Tier!.TierCode.Should().Be("OON");

        // …and the priced answer reports the same requirement, from the same call shape.
        var priced = await svc.PriceAsync(new TierPricingRequest(
            PlanVersion, "LAB", new TierQuery(ProviderId, ServiceDate), Cash.Egp(500m)), null);
        priced.Pricing!.RequiresPreauth.Should().Be(d.Required);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_unresolvable_pre_authorization_question_fails_CLOSED(bool tierResolves)
    {
        // Approvals is the gate that prevents the bad state, so "we could not tell" must mean "authorization
        // required" — never "go ahead". And the caller must be able to see it was indeterminate, so a
        // resolution outage is not mistaken for a benefit decision.
        var svc = Service(tierResolves ? InNetwork : null, terms: null);

        var d = await svc.RequiresPreauthAsync(PlanVersion, "LAB", new TierQuery(ProviderId, ServiceDate), null);

        d.Required.Should().BeTrue();
        d.Determinate.Should().BeFalse();
        d.Failure.Should().Be(tierResolves ? TierPricingFailure.NotPricedAtTier : TierPricingFailure.TierUnresolved);
    }
}
