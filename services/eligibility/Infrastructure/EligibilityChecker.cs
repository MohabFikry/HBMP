using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mersal.Eligibility.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Eligibility.Infrastructure;

/// <summary>Serialized shape of a coverage limit inside <see cref="CoverageProjection.LimitsJson"/>.</summary>
public sealed record LimitStateDto(string LimitType, decimal LimitValue, decimal ConsumedValue);

/// <summary>The cache-first check result plus whether it was served from cache (for latency/audit).</summary>
public sealed record CheckOutcome(EligibilityResult Result, DateTimeOffset ExpiresAt, bool FromCache);

/// <summary>
/// Read-through eligibility resolver: cache → projection → engine → snapshot + cache. Reads the
/// member + coverage projections owned by this service, computes via the pure engine, persists the
/// derived snapshot, and caches it under the configured TTL.
/// </summary>
public sealed class EligibilityChecker(EligibilityDbContext db, IEligibilityCache cache, TimeProvider clock)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    public async Task<CheckOutcome> CheckAsync(
        Guid beneficiaryId, string benefitCategory, string? serviceCode, bool serviceRequiresPreAuth,
        CancellationToken ct = default)
    {
        // 18.A3 (X9): the key carries every input the engine branches on — a non-gated answer can no
        // longer be served for a gated service.
        var cacheKey = new EligibilityCacheKey(beneficiaryId, benefitCategory, serviceCode, serviceRequiresPreAuth);
        var cached = await cache.GetAsync(cacheKey, ct);
        if (cached is not null)
        {
            var snap = JsonSerializer.Deserialize<EligibilitySnapshot>(cached, Json)!;
            return new CheckOutcome(Rehydrate(snap), snap.ExpiresAt, FromCache: true);
        }

        var result = await ComputeAsync(beneficiaryId, benefitCategory, serviceCode, serviceRequiresPreAuth, ct);
        var now = clock.GetUtcNow();
        var expires = now.Add(Ttl);

        var snapshot = new EligibilitySnapshot
        {
            SnapshotId = Guid.NewGuid(),
            BeneficiaryId = beneficiaryId,
            BenefitCategory = benefitCategory,
            Decision = result.Decision.ToString(),
            CoverageId = result.CoverageId,
            LimitStateJson = JsonSerializer.Serialize(result.LimitState, Json),
            ReasonsJson = JsonSerializer.Serialize(result.Reasons, Json),
            VersionHash = Hash(result),
            ComputedAt = now,
            ExpiresAt = expires,
        };

        // Persist derived snapshot (latest wins per beneficiary+category).
        var existing = await db.Snapshots
            .Where(s => s.BeneficiaryId == beneficiaryId && s.BenefitCategory == benefitCategory)
            .ToListAsync(ct);
        db.Snapshots.RemoveRange(existing);
        db.Snapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);

        await cache.SetAsync(cacheKey, JsonSerializer.Serialize(snapshot, Json), Ttl, ct);
        return new CheckOutcome(result, expires, FromCache: false);
    }

    private async Task<EligibilityResult> ComputeAsync(
        Guid beneficiaryId, string benefitCategory, string? serviceCode, bool serviceRequiresPreAuth, CancellationToken ct)
    {
        var member = await db.Members.AsNoTracking().FirstOrDefaultAsync(m => m.BeneficiaryId == beneficiaryId, ct);
        if (member is null)
            return new EligibilityResult(EligibilityDecision.Ineligible, null, ["unknown beneficiary"], null);

        var status = Enum.TryParse<MemberStatus>(member.Status, out var s) ? s : MemberStatus.Inactive;

        var covRows = await db.Coverages.AsNoTracking().Where(c => c.BeneficiaryId == beneficiaryId).ToListAsync(ct);
        var coverages = covRows.Select(c => new CoverageView(
            c.CoverageId, c.BenefitCategory,
            CoverageActive: string.Equals(c.Status, "Active", StringComparison.OrdinalIgnoreCase),
            c.EffectiveFrom, c.EffectiveTo,
            (JsonSerializer.Deserialize<List<LimitStateDto>>(c.LimitsJson, Json) ?? [])
                .Select(l => new LimitState(Enum.Parse<LimitType>(l.LimitType), l.LimitValue, l.ConsumedValue))
                .ToList())).ToList();

        var onDate = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        return EligibilityEngine.Evaluate(new EligibilityRequest(
            status, benefitCategory, serviceCode, serviceRequiresPreAuth, coverages, onDate));
    }

    private static EligibilityResult Rehydrate(EligibilitySnapshot snap) => new(
        Enum.Parse<EligibilityDecision>(snap.Decision),
        snap.CoverageId,
        JsonSerializer.Deserialize<List<string>>(snap.ReasonsJson, Json) ?? [],
        JsonSerializer.Deserialize<LimitState?>(snap.LimitStateJson, Json));

    private static string Hash(EligibilityResult r)
    {
        var canonical = $"{r.Decision}|{r.CoverageId}|{string.Join(',', r.Reasons)}|{r.LimitState?.Remaining}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }
}
