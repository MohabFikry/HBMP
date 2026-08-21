using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mersal.Eligibility.Domain;
using Microsoft.EntityFrameworkCore;
using Mersal.Time;

namespace Mersal.Eligibility.Infrastructure;

/// <summary>Serialized shape of a coverage limit inside <see cref="CoverageProjection.LimitsJson"/>.</summary>
public sealed record LimitStateDto(string LimitType, decimal LimitValue, decimal ConsumedValue);

/// <summary>The cache-first check result plus whether it was served from cache (for latency/audit).</summary>
public sealed record CheckOutcome(EligibilityResult Result, DateTimeOffset ExpiresAt, bool FromCache);

/// <summary>Where and when the care happens — the dimensions a network tier is resolved on (19.1b). Absent
/// when the caller asks a provider-independent question, which is a DIFFERENT question and is cached as one.</summary>
public readonly record struct EligibilityTierContext(Guid ProviderId, DateOnly ServiceDate, Guid? LocationId);

/// <summary>
/// Read-through eligibility resolver: cache → projection → engine → snapshot + cache. Reads the
/// member + coverage projections owned by this service, computes via the pure engine, persists the
/// derived snapshot, and caches it under the configured TTL.
/// </summary>
public sealed class EligibilityChecker(EligibilityDbContext db, IEligibilityCache cache, TimeProvider clock,
    IBusinessCalendar calendar)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    /// <param name="tier">19.1b — where and when the care happens. It reaches the cache key because the tier
    /// can make a service GATED that is open-access elsewhere (<c>requires_preauth_override</c>), so two
    /// providers are two different questions with two different right answers.</param>
    public async Task<CheckOutcome> CheckAsync(
        Guid beneficiaryId, string benefitCategory, string? serviceCode, bool serviceRequiresPreAuth,
        EligibilityTierContext? tier = null, CancellationToken ct = default)
    {
        // 18.A3 (X9): the key carries every input the engine branches on — a non-gated answer can no
        // longer be served for a gated service. 19.1b extends the same rule to provider/location/service date.
        var cacheKey = new EligibilityCacheKey(
            beneficiaryId, benefitCategory, serviceCode, serviceRequiresPreAuth,
            tier?.ProviderId, tier?.LocationId, tier?.ServiceDate);
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

    /// <summary>
    /// 32.6 — the answer when no benefit category was named.
    ///
    /// <para>This is a MEMBERSHIP verdict, and the caller is told so in
    /// <c>EligibilityCheckResponse.DecisionScope</c>. It runs the first of the engine's rules — only an Active
    /// member can ever be Eligible — and stops there, because every rule after it needs a category. It
    /// deliberately does not guess one: picking "CONSULTATION" for a walk-in would answer a question nobody
    /// asked and answer it about a specific benefit.</para>
    ///
    /// <para>NO SNAPSHOT IS WRITTEN. A snapshot row is per beneficiary AND category (that is its key), so
    /// there is no row this belongs in; writing one under an invented category would corrupt the next real
    /// check for it. The expiry still comes back so the caller knows how long the answer stands.</para>
    /// </summary>
    public async Task<CheckOutcome> CheckMembershipAsync(Guid beneficiaryId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var expires = now.Add(Ttl);

        var member = await db.Members.AsNoTracking().FirstOrDefaultAsync(m => m.BeneficiaryId == beneficiaryId, ct);
        if (member is null)
            return new CheckOutcome(
                new EligibilityResult(EligibilityDecision.Ineligible, null, ["unknown beneficiary"], null),
                expires, FromCache: false);

        var status = Enum.TryParse<MemberStatus>(member.Status, out var s) ? s : MemberStatus.Inactive;

        // An unparseable status falls to Inactive, as it does on the category path. A status this service
        // cannot read is not a reason to say yes.
        return status == MemberStatus.Active
            ? new CheckOutcome(
                new EligibilityResult(EligibilityDecision.Eligible, null,
                    [
                        "member status is Active",
                        // Said in the payload, not left to the reader. The desk sees "Eligible" and the
                        // sentence that bounds it in the same breath.
                        "no benefit category was named, so nothing here says whether a particular service is covered",
                    ], null),
                expires, FromCache: false)
            : new CheckOutcome(
                new EligibilityResult(EligibilityDecision.Ineligible, null, [$"member status is {status}"], null),
                expires, FromCache: false);
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
                .ToList(),
            // 19.2 — WITHOUT THIS THE WAITING-PERIOD BRANCH IS DEAD CODE. CoverageView defaults the boundary
            // to null so existing callers compile unchanged, which is exactly why its absence here was silent:
            // the engine's own tests pass the date, and the only production caller did not.
            c.WaitingPeriodEndsOn)).ToList();

        var onDate = calendar.Today();   // 18.A3 — coverage validity is a Cairo date
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
