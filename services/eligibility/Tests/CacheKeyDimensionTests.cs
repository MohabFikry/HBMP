using FluentAssertions;
using Mersal.Eligibility.Infrastructure;

namespace Mersal.Eligibility.Tests;

/// <summary>
/// Phase 19.1b — the cache key carries every dimension the answer depends on.
///
/// This is the X9 lesson from the phase-18 audit, applied one layer down. X9 was: the key was
/// (beneficiaryId, benefitCategory), but the engine also branched on the service code and whether the service
/// was pre-auth gated — so a non-gated <c>Eligible</c> was served for a gated service for the full TTL, a
/// silent pre-authorization bypass that nothing reported.
///
/// 19.1b introduces three more dimensions. The same beneficiary, asking about the same service, now gets a
/// different COST SHARE depending on which provider and location they are standing in and what date the care
/// is on — because a provider's tier is effective-dated and a tier can also make a service gated that is
/// open-access elsewhere. Leave any of them out and one hospital's co-pay is quoted for another's, to a person
/// at a counter who has no way to check it.
///
/// The rule is one sentence: never key a cache on fewer dimensions than the decision depends on. These tests
/// hold it by construction — every dimension must change the key.
/// </summary>
public class CacheKeyDimensionTests
{
    private static readonly Guid Beneficiary = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProviderA = Guid.Parse("22222222-2222-2222-2222-222222222221");
    private static readonly Guid ProviderB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LocationA = Guid.Parse("33333333-3333-3333-3333-333333333331");
    private static readonly Guid LocationB = Guid.Parse("33333333-3333-3333-3333-333333333332");
    private static readonly DateOnly Feb = new(2026, 2, 10);
    private static readonly DateOnly Mar = new(2026, 3, 10);

    private static EligibilityCacheKey Key(
        string category = "LAB", string? serviceCode = "85025", bool gated = false,
        Guid? provider = null, Guid? location = null, DateOnly? date = null) =>
        new(Beneficiary, category, serviceCode, gated, provider, location, date);

    private static readonly EligibilityCacheKey Baseline =
        new(Beneficiary, "LAB", "85025", false, ProviderA, LocationA, Feb);

    [Fact]
    public void Two_providers_are_two_different_questions()
    {
        // The headline case. Provider A may be T1 (10%) and provider B out-of-network (40%) for the very same
        // service on the very same day.
        CacheKey.For(Baseline).Should().NotBe(CacheKey.For(Baseline with { ProviderId = ProviderB }));
    }

    [Fact]
    public void Two_locations_of_the_same_provider_are_two_different_questions()
    {
        // A location assignment OVERRIDES its parent provider (most-specific-wins), so two branches of one
        // hospital can sit in different tiers.
        CacheKey.For(Baseline).Should().NotBe(CacheKey.For(Baseline with { LocationId = LocationB }));
    }

    [Fact]
    public void Two_service_dates_are_two_different_questions()
    {
        // Tier assignments are effective-dated. February's answer is not March's answer if the provider moved.
        CacheKey.For(Baseline).Should().NotBe(CacheKey.For(Baseline with { ServiceDate = Mar }));
    }

    [Fact]
    public void A_provider_specific_question_never_collides_with_a_provider_independent_one()
    {
        // "Is this member covered for Lab at all?" and "what will they pay at this hospital?" are different
        // questions. Rendering the absent dimensions as a placeholder rather than omitting them is what keeps
        // the two key shapes from ever coinciding.
        var general = Key(provider: null, location: null, date: null);

        CacheKey.For(general).Should().NotBe(CacheKey.For(Baseline));
    }

    [Fact]
    public void The_original_X9_dimensions_are_still_in_the_key()
    {
        // Regression guard: 19.1b must not have quietly dropped what 18.A3 added.
        CacheKey.For(Baseline).Should().NotBe(CacheKey.For(Baseline with { RequiresPreAuth = true }));
        CacheKey.For(Baseline).Should().NotBe(CacheKey.For(Baseline with { ServiceCode = "80053" }));
        CacheKey.For(Baseline).Should().NotBe(CacheKey.For(Baseline with { BenefitCategory = "PHARMACY" }));
    }

    [Fact]
    public void Identical_questions_share_an_answer()
    {
        // The other half: over-keying would make the cache useless, so the same question must still hit.
        CacheKey.For(Baseline).Should().Be(CacheKey.For(
            new EligibilityCacheKey(Beneficiary, "LAB", "85025", false, ProviderA, LocationA, Feb)));
    }

    [Fact]
    public void Every_dimension_of_the_key_type_reaches_the_rendered_string()
    {
        // Reflection over the key's own shape, so ADDING a dimension without threading it into CacheKey.For
        // fails here rather than silently reintroducing X9. A field that does not change the string is a
        // dimension the cache cannot see.
        var properties = typeof(EligibilityCacheKey).GetProperties();
        properties.Should().HaveCount(7, "the key carries beneficiary, category, service code, gating, provider, location and service date");

        var variants = new[]
        {
            Baseline with { BeneficiaryId = Guid.NewGuid() },
            Baseline with { BenefitCategory = "IMAGING" },
            Baseline with { ServiceCode = "71046" },
            Baseline with { RequiresPreAuth = true },
            Baseline with { ProviderId = ProviderB },
            Baseline with { LocationId = LocationB },
            Baseline with { ServiceDate = Mar },
        };

        variants.Select(CacheKey.For).Append(CacheKey.For(Baseline))
            .Should().OnlyHaveUniqueItems("changing any single dimension must change the cache key");
    }
}
