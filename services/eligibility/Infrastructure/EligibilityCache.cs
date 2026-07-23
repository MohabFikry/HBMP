using System.Collections.Concurrent;
using StackExchange.Redis;

namespace Mersal.Eligibility.Infrastructure;

/// <summary>
/// Cache-first eligibility snapshot store, keyed by (beneficiaryId, benefitCategory). A check serves
/// the cached snapshot within TTL; upstream policy/coverage/status events invalidate it so the next
/// check recomputes. Snapshots are derived — the cache is an optimization, never a source of truth.
/// </summary>
public interface IEligibilityCache
{
    Task<string?> GetAsync(Guid beneficiaryId, string benefitCategory, CancellationToken ct = default);
    Task SetAsync(Guid beneficiaryId, string benefitCategory, string json, TimeSpan ttl, CancellationToken ct = default);
    /// <summary>Invalidate every cached snapshot for a beneficiary (all categories).</summary>
    Task InvalidateAsync(Guid beneficiaryId, CancellationToken ct = default);
}

public static class CacheKey
{
    public static string For(Guid beneficiaryId, string benefitCategory) =>
        $"elig:{beneficiaryId:N}:{benefitCategory.ToUpperInvariant()}";

    public static string Set(Guid beneficiaryId) => $"elig:set:{beneficiaryId:N}";
}

/// <summary>Valkey/Redis-backed cache (Tier 1+). Tracks a per-beneficiary key set for invalidation.</summary>
public sealed class ValkeyEligibilityCache(IConnectionMultiplexer mux) : IEligibilityCache
{
    private IDatabase Db => mux.GetDatabase();

    public async Task<string?> GetAsync(Guid beneficiaryId, string benefitCategory, CancellationToken ct = default)
    {
        var v = await Db.StringGetAsync(CacheKey.For(beneficiaryId, benefitCategory));
        return v.IsNullOrEmpty ? null : v.ToString();
    }

    public async Task SetAsync(Guid beneficiaryId, string benefitCategory, string json, TimeSpan ttl, CancellationToken ct = default)
    {
        var key = CacheKey.For(beneficiaryId, benefitCategory);
        await Db.StringSetAsync(key, json, ttl);
        await Db.SetAddAsync(CacheKey.Set(beneficiaryId), key);
        await Db.KeyExpireAsync(CacheKey.Set(beneficiaryId), ttl + TimeSpan.FromMinutes(5));
    }

    public async Task InvalidateAsync(Guid beneficiaryId, CancellationToken ct = default)
    {
        var setKey = CacheKey.Set(beneficiaryId);
        var members = await Db.SetMembersAsync(setKey);
        foreach (var m in members) await Db.KeyDeleteAsync(m.ToString());
        await Db.KeyDeleteAsync(setKey);
    }
}

/// <summary>In-memory fallback for tests / single-node dev without Valkey.</summary>
public sealed class InMemoryEligibilityCache : IEligibilityCache
{
    private readonly ConcurrentDictionary<string, (string Json, DateTimeOffset Expires)> _store = new();

    public Task<string?> GetAsync(Guid beneficiaryId, string benefitCategory, CancellationToken ct = default)
    {
        var key = CacheKey.For(beneficiaryId, benefitCategory);
        if (_store.TryGetValue(key, out var e) && e.Expires > DateTimeOffset.UtcNow)
            return Task.FromResult<string?>(e.Json);
        _store.TryRemove(key, out _);
        return Task.FromResult<string?>(null);
    }

    public Task SetAsync(Guid beneficiaryId, string benefitCategory, string json, TimeSpan ttl, CancellationToken ct = default)
    {
        _store[CacheKey.For(beneficiaryId, benefitCategory)] = (json, DateTimeOffset.UtcNow.Add(ttl));
        return Task.CompletedTask;
    }

    public Task InvalidateAsync(Guid beneficiaryId, CancellationToken ct = default)
    {
        var prefix = $"elig:{beneficiaryId:N}:";
        foreach (var k in _store.Keys)
            if (k.StartsWith(prefix, StringComparison.Ordinal)) _store.TryRemove(k, out _);
        return Task.CompletedTask;
    }
}
