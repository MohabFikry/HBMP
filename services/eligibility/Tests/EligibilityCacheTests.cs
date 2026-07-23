using FluentAssertions;
using Mersal.Eligibility.Infrastructure;

namespace Mersal.Eligibility.Tests;

public class EligibilityCacheTests
{
    private static readonly Guid Ben = Guid.NewGuid();

    [Fact]
    public async Task Get_returns_null_on_miss()
    {
        var cache = new InMemoryEligibilityCache();
        (await cache.GetAsync(Ben, "CONSULT")).Should().BeNull();
    }

    [Fact]
    public async Task Set_then_Get_is_a_hit_within_ttl()
    {
        var cache = new InMemoryEligibilityCache();
        await cache.SetAsync(Ben, "CONSULT", "{\"decision\":\"Eligible\"}", TimeSpan.FromMinutes(5));
        (await cache.GetAsync(Ben, "CONSULT")).Should().Contain("Eligible");
    }

    [Fact]
    public async Task Expired_entry_is_a_miss()
    {
        var cache = new InMemoryEligibilityCache();
        await cache.SetAsync(Ben, "CONSULT", "x", TimeSpan.FromMilliseconds(-1));
        (await cache.GetAsync(Ben, "CONSULT")).Should().BeNull();
    }

    [Fact]
    public async Task Invalidate_clears_all_categories_for_the_beneficiary_only()
    {
        var cache = new InMemoryEligibilityCache();
        var other = Guid.NewGuid();
        await cache.SetAsync(Ben, "CONSULT", "a", TimeSpan.FromMinutes(5));
        await cache.SetAsync(Ben, "PHARMACY", "b", TimeSpan.FromMinutes(5));
        await cache.SetAsync(other, "CONSULT", "c", TimeSpan.FromMinutes(5));

        await cache.InvalidateAsync(Ben);

        (await cache.GetAsync(Ben, "CONSULT")).Should().BeNull();
        (await cache.GetAsync(Ben, "PHARMACY")).Should().BeNull();
        (await cache.GetAsync(other, "CONSULT")).Should().Be("c"); // untouched
    }

    [Fact]
    public void Cache_keys_are_category_case_insensitive()
        => CacheKey.For(Ben, "consult").Should().Be(CacheKey.For(Ben, "CONSULT"));
}
