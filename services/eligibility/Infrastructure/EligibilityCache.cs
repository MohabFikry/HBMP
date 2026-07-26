using System.Collections.Concurrent;
using StackExchange.Redis;

namespace Mersal.Eligibility.Infrastructure;

/// <summary>
/// Every input the decision depends on. 18.A3 (audit R2 X9): the key used to be
/// (beneficiaryId, benefitCategory) alone, but <c>EligibilityEngine</c> also branches on the service
/// code and whether the service is pre-auth GATED. A cached non-gated <c>Eligible</c> was therefore
/// served for a gated service for the full 15-minute TTL — a silent pre-authorization bypass. The key
/// now carries the whole decision input, so two different questions can never share an answer.
/// </summary>
public readonly record struct EligibilityCacheKey(
    Guid BeneficiaryId, string BenefitCategory, string? ServiceCode, bool RequiresPreAuth);

/// <summary>
/// Cache-first eligibility snapshot store. A check serves the cached snapshot within TTL; upstream
/// policy/coverage/status events invalidate every entry for the beneficiary so the next check
/// recomputes. Snapshots are derived — the cache is an optimization, never a source of truth.
/// </summary>
public interface IEligibilityCache
{
    Task<string?> GetAsync(EligibilityCacheKey key, CancellationToken ct = default);
    Task SetAsync(EligibilityCacheKey key, string json, TimeSpan ttl, CancellationToken ct = default);
    /// <summary>Invalidate every cached snapshot for a beneficiary (all categories and services).</summary>
    Task InvalidateAsync(Guid beneficiaryId, CancellationToken ct = default);
}

public static class CacheKey
{
    public static string For(EligibilityCacheKey k) =>
        $"elig:{k.BeneficiaryId:N}:{k.BenefitCategory.ToUpperInvariant()}" +
        $":{(string.IsNullOrWhiteSpace(k.ServiceCode) ? "-" : k.ServiceCode.ToUpperInvariant())}" +
        $":{(k.RequiresPreAuth ? "gated" : "open")}";

    public static string Set(Guid beneficiaryId) => $"elig:set:{beneficiaryId:N}";
}

/// <summary>Valkey/Redis-backed cache (Tier 1+). Tracks a per-beneficiary key set for invalidation.</summary>
public sealed class ValkeyEligibilityCache(IConnectionMultiplexer mux) : IEligibilityCache
{
    private IDatabase Db => mux.GetDatabase();

    public async Task<string?> GetAsync(EligibilityCacheKey key, CancellationToken ct = default)
    {
        var v = await Db.StringGetAsync(CacheKey.For(key));
        return v.IsNullOrEmpty ? null : v.ToString();
    }

    public async Task SetAsync(EligibilityCacheKey key, string json, TimeSpan ttl, CancellationToken ct = default)
    {
        var k = CacheKey.For(key);
        await Db.StringSetAsync(k, json, ttl);
        await Db.SetAddAsync(CacheKey.Set(key.BeneficiaryId), k);
        await Db.KeyExpireAsync(CacheKey.Set(key.BeneficiaryId), ttl + TimeSpan.FromMinutes(5));
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
public sealed class InMemoryEligibilityCache(TimeProvider clock) : IEligibilityCache
{
    /// <summary>Parameterless ctor for the DI default; tests may pin the clock.</summary>
    public InMemoryEligibilityCache() : this(TimeProvider.System) { }

    private readonly ConcurrentDictionary<string, (string Json, DateTimeOffset Expires)> _store = new();

    public Task<string?> GetAsync(EligibilityCacheKey key, CancellationToken ct = default)
    {
        var k = CacheKey.For(key);
        if (_store.TryGetValue(k, out var e) && e.Expires > clock.GetUtcNow())
            return Task.FromResult<string?>(e.Json);
        _store.TryRemove(k, out _);
        return Task.FromResult<string?>(null);
    }

    public Task SetAsync(EligibilityCacheKey key, string json, TimeSpan ttl, CancellationToken ct = default)
    {
        _store[CacheKey.For(key)] = (json, clock.GetUtcNow().Add(ttl));
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
