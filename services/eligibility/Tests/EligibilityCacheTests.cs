using FluentAssertions;
using Mersal.Eligibility.Infrastructure;

namespace Mersal.Eligibility.Tests;

public class EligibilityCacheTests
{
    private static readonly Guid Ben = Guid.NewGuid();

    private static EligibilityCacheKey Key(Guid ben, string category, string? serviceCode = null, bool gated = false) =>
        new(ben, category, serviceCode, gated);

    [Fact]
    public async Task Get_returns_null_on_miss()
    {
        var cache = new InMemoryEligibilityCache();
        (await cache.GetAsync(Key(Ben, "CONSULT"))).Should().BeNull();
    }

    [Fact]
    public async Task Set_then_Get_is_a_hit_within_ttl()
    {
        var cache = new InMemoryEligibilityCache();
        await cache.SetAsync(Key(Ben, "CONSULT"), "{\"decision\":\"Eligible\"}", TimeSpan.FromMinutes(5));
        (await cache.GetAsync(Key(Ben, "CONSULT"))).Should().Contain("Eligible");
    }

    [Fact]
    public async Task Expired_entry_is_a_miss()
    {
        var cache = new InMemoryEligibilityCache();
        await cache.SetAsync(Key(Ben, "CONSULT"), "x", TimeSpan.FromMilliseconds(-1));
        (await cache.GetAsync(Key(Ben, "CONSULT"))).Should().BeNull();
    }

    [Fact]
    public async Task Invalidate_clears_all_categories_for_the_beneficiary_only()
    {
        var cache = new InMemoryEligibilityCache();
        var other = Guid.NewGuid();
        await cache.SetAsync(Key(Ben, "CONSULT"), "a", TimeSpan.FromMinutes(5));
        await cache.SetAsync(Key(Ben, "PHARMACY"), "b", TimeSpan.FromMinutes(5));
        await cache.SetAsync(Key(other, "CONSULT"), "c", TimeSpan.FromMinutes(5));

        await cache.InvalidateAsync(Ben);

        (await cache.GetAsync(Key(Ben, "CONSULT"))).Should().BeNull();
        (await cache.GetAsync(Key(Ben, "PHARMACY"))).Should().BeNull();
        (await cache.GetAsync(Key(other, "CONSULT"))).Should().Be("c"); // untouched
    }

    [Fact]
    public void Cache_keys_are_category_case_insensitive()
        => CacheKey.For(Key(Ben, "consult")).Should().Be(CacheKey.For(Key(Ben, "CONSULT")));

    // ── 18.A3 / audit R2 X9 — the key must carry the whole decision input ─────────────────────────

    [Fact]
    public async Task A_cached_non_gated_answer_is_never_served_for_a_gated_service()
    {
        var cache = new InMemoryEligibilityCache();
        // A routine consult was checked and cached as Eligible…
        await cache.SetAsync(Key(Ben, "CONSULT", "99213", gated: false), "{\"decision\":\"Eligible\"}", TimeSpan.FromMinutes(15));

        // …the same beneficiary and category, but a PRE-AUTH GATED service, must miss and re-run the engine.
        (await cache.GetAsync(Key(Ben, "CONSULT", "99213", gated: true)))
            .Should().BeNull("a gated service must never inherit a non-gated Eligible");
    }

    [Fact]
    public async Task Two_different_services_in_one_category_do_not_share_an_answer()
    {
        var cache = new InMemoryEligibilityCache();
        await cache.SetAsync(Key(Ben, "LAB", "80053"), "cheap-panel", TimeSpan.FromMinutes(15));

        (await cache.GetAsync(Key(Ben, "LAB", "70553"))).Should().BeNull();
        (await cache.GetAsync(Key(Ben, "LAB", "80053"))).Should().Be("cheap-panel");
    }

    [Fact]
    public void Every_decision_input_participates_in_the_key()
    {
        var baseline = CacheKey.For(Key(Ben, "LAB", "80053", gated: false));

        CacheKey.For(Key(Guid.NewGuid(), "LAB", "80053", gated: false)).Should().NotBe(baseline);
        CacheKey.For(Key(Ben, "IMAGING", "80053", gated: false)).Should().NotBe(baseline);
        CacheKey.For(Key(Ben, "LAB", "70553", gated: false)).Should().NotBe(baseline);
        CacheKey.For(Key(Ben, "LAB", "80053", gated: true)).Should().NotBe(baseline);
        CacheKey.For(Key(Ben, "LAB", null, gated: false)).Should().NotBe(baseline);
    }

    [Fact]
    public async Task Invalidation_still_clears_every_service_variant_for_the_beneficiary()
    {
        var cache = new InMemoryEligibilityCache();
        await cache.SetAsync(Key(Ben, "LAB", "80053", gated: false), "a", TimeSpan.FromMinutes(15));
        await cache.SetAsync(Key(Ben, "LAB", "70553", gated: true), "b", TimeSpan.FromMinutes(15));

        await cache.InvalidateAsync(Ben);

        (await cache.GetAsync(Key(Ben, "LAB", "80053", gated: false))).Should().BeNull();
        (await cache.GetAsync(Key(Ben, "LAB", "70553", gated: true))).Should().BeNull();
    }
}
