using Mersal.BenefitPricing;
using Mersal.Policy.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Infrastructure;

// Phase 19.4 — the utilization read model, as a DIRECT QUERY over the accumulator (design 38 §4.3).
//
// ============================================================================================================
// WHY A DIRECT QUERY AND NOT A PROJECTION (ADR-0023)
// ============================================================================================================
// The build prompt allows either and asks for the choice to be stated. Direct query, because the single
// non-negotiable requirement is that every response reconciles EXACTLY to coverage_limit.consumed_value — and
// a projection makes that a property somebody has to keep true, whereas a direct read makes it a property that
// cannot become false.
//
// A projection would introduce precisely the drift the reconciliation requirement exists to forbid: a missed
// event, a replay bug, or a rebuild that ran against a different window, and the report quietly disagrees with
// the number eligibility is refusing care on. Nobody notices, because noticing means comparing two things
// nobody compares — the same failure mode 19.3c's timeline avoided by being derived rather than maintained.
//
// If latency ever demands a projection, the reconciliation test in this phase is what will make it safe to
// add: it already asserts the invariant a projection would have to preserve.

/// <summary>The scope a utilization question is asked over. Resolved to a member set before any accumulator is
/// touched, so every scope shares one aggregation path and cannot drift into five slightly different sums.</summary>
public enum UtilizationScope { Member, Group, Plan, Policy, Payer }

/// <summary>A member of the scope, with just enough to render the per-member table.</summary>
public sealed record ScopeMember(
    Guid EnrollmentId, Guid BeneficiaryId, string MemberNo, Guid PolicyId, Guid PolicyPlanId, Guid? GroupId,
    EnrollmentStatus Status);

public sealed class UtilizationQuery(PolicyDbContext db, INetworkTierResolver tiers)
{
    /// <summary>
    /// The live members of a scope.
    ///
    /// Terminated and Cancelled memberships are EXCLUDED from the member list but their consumption still
    /// appears in the scope totals, because the benefit was genuinely used and the money genuinely left. A
    /// report that drops a terminated member's spend understates every period in which anybody left.
    /// </summary>
    public async Task<IReadOnlyList<ScopeMember>> MembersAsync(
        UtilizationScope scope, Guid scopeId, bool includeInactive, CancellationToken ct = default)
    {
        var q = db.Enrollments.AsNoTracking().Where(e => !e.IsDeleted);

        q = scope switch
        {
            UtilizationScope.Member => q.Where(e => e.BeneficiaryId == scopeId),
            UtilizationScope.Group => q.Where(e => e.GroupId == scopeId),
            UtilizationScope.Plan => q.Where(e => e.PolicyPlanId == scopeId),
            UtilizationScope.Policy => q.Where(e => e.PolicyId == scopeId),
            // Payer is one hop up: the policies this payer holds, then their members. Kept as a subquery so
            // the whole thing stays one round trip.
            UtilizationScope.Payer => q.Where(e =>
                db.Policies.Where(p => p.PayerId == scopeId && !p.IsDeleted).Select(p => p.PolicyId)
                    .Contains(e.PolicyId)),
            _ => q.Where(_ => false),
        };

        if (!includeInactive)
            q = q.Where(e => e.Status == EnrollmentStatus.Active || e.Status == EnrollmentStatus.Suspended);

        return await q
            .OrderBy(e => e.MemberNo)
            .Select(e => new ScopeMember(
                e.EnrollmentId, e.BeneficiaryId, e.MemberNo, e.PolicyId, e.PolicyPlanId, e.GroupId, e.Status))
            .ToListAsync(ct);
    }

    /// <summary>
    /// One member's accumulator, per benefit category — read STRAIGHT off coverage_limit.
    ///
    /// A category with an active coverage but no accumulating limit is UNLIMITED and is returned with a null
    /// limit rather than omitted: "covered, uncapped" and "not covered at all" are opposite answers, and
    /// dropping the row would render them identically.
    /// </summary>
    public async Task<IReadOnlyList<CategoryAccumulator>> MemberAccumulatorsAsync(
        Guid beneficiaryId, DateOnly asOf, CancellationToken ct = default)
    {
        var rows = await db.Coverages.AsNoTracking().Include(c => c.Limits)
            .Where(c => c.BeneficiaryId == beneficiaryId && !c.IsDeleted)
            .Join(db.BenefitCategories.AsNoTracking(),
                c => c.BenefitCategoryId, bc => bc.BenefitCategoryId,
                (c, bc) => new { Coverage = c, Category = bc })
            .ToListAsync(ct);

        var result = new List<CategoryAccumulator>();
        foreach (var row in rows)
        {
            var c = row.Coverage;
            if (!BenefitAccumulation.IsApplicable(c.Status, c.IsDeleted, c.EffectiveFrom, c.EffectiveTo, asOf))
                continue;

            var accumulating = c.Limits.Where(l => BenefitAccumulation.Accumulates(l.LimitType)).ToList();
            if (accumulating.Count == 0)
            {
                result.Add(new CategoryAccumulator(
                    row.Category.Code, c.CoverageId, null, null, null, 0m, "EGP", ResetPeriod.None, null, null));
                continue;
            }

            foreach (var l in accumulating)
            {
                result.Add(new CategoryAccumulator(
                    row.Category.Code, c.CoverageId, l.CoverageLimitId, l.LimitType, l.LimitValue,
                    l.ConsumedValue, l.CurrencyCode, l.ResetPeriod, l.LastResetOn,
                    UtilizationMath.NextResetOn(l.ResetPeriod, l.LimitType, asOf)));
            }
        }

        return [.. result.OrderBy(r => r.BenefitCategoryCode, StringComparer.Ordinal)
                         .ThenBy(r => r.LimitType?.ToString() ?? "", StringComparer.Ordinal)];
    }

    /// <summary>
    /// Per-member totals for a scope table, summed from the same accumulator rows the member view reports.
    ///
    /// One query for all members rather than N calls to <see cref="MemberAccumulatorsAsync"/>: a 4 000-member
    /// policy is a normal scope here, and a per-member round trip turns a report into an outage.
    /// </summary>
    public async Task<IReadOnlyList<MemberUtilization>> MemberTotalsAsync(
        IReadOnlyCollection<ScopeMember> members, DateOnly asOf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0) return [];

        var ids = members.Select(m => m.BeneficiaryId).Distinct().ToList();

        var coverages = await db.Coverages.AsNoTracking().Include(c => c.Limits)
            .Where(c => ids.Contains(c.BeneficiaryId) && !c.IsDeleted
                        && c.Status == CoverageStatus.Active
                        && c.EffectiveFrom <= asOf && (c.EffectiveTo == null || c.EffectiveTo >= asOf))
            .ToListAsync(ct);

        var byBeneficiary = coverages.GroupBy(c => c.BeneficiaryId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<MemberUtilization>(members.Count);
        foreach (var m in members)
        {
            decimal limit = 0m, consumed = 0m;
            var anyUnlimited = false;

            if (byBeneficiary.TryGetValue(m.BeneficiaryId, out var covs))
            {
                foreach (var c in covs)
                {
                    var accumulating = c.Limits.Where(l => BenefitAccumulation.Accumulates(l.LimitType)).ToList();
                    if (accumulating.Count == 0) { anyUnlimited = true; continue; }
                    foreach (var l in accumulating)
                    {
                        limit += l.LimitValue;
                        consumed += l.ConsumedValue;
                    }
                }
            }

            result.Add(new MemberUtilization(
                m.EnrollmentId, m.BeneficiaryId, m.MemberNo, m.PolicyPlanId, m.GroupId,
                limit, consumed, anyUnlimited));
        }

        return result;
    }

    /// <summary>
    /// The network-tier split for a set of members over a service-date window.
    ///
    /// Movements are read from the LEDGER, not the accumulator, because the accumulator has no memory of where
    /// care was delivered — and because a tier split is inherently a window question ("where did this quarter's
    /// volume go"), whereas the accumulator is a point-in-time balance.
    ///
    /// Tiers are resolved once per distinct (provider, service date) pair. A member seeing the same clinic
    /// twelve times is one resolution, not twelve; and the resolution is at the SERVICE date, so a provider
    /// that moved tier in March does not retroactively re-tier February.
    /// </summary>
    public async Task<IReadOnlyList<TierUtilization>> TierSplitAsync(
        IReadOnlyCollection<Guid> beneficiaryIds, DateOnly from, DateOnly to,
        string? bearerToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(beneficiaryIds);
        if (beneficiaryIds.Count == 0) return [];

        var ids = beneficiaryIds.ToList();
        var movements = await db.BenefitConsumptions.AsNoTracking()
            .Where(r => ids.Contains(r.BeneficiaryId)
                        && r.ServiceDate != null && r.ServiceDate >= from && r.ServiceDate <= to
                        && (r.Outcome == ConsumptionOutcome.Applied || r.Outcome == ConsumptionOutcome.Reversed))
            .Select(r => new { r.ProviderId, r.ProviderLocationId, r.ServiceDate, r.Quantity, r.Direction })
            .ToListAsync(ct);

        var cache = new Dictionary<(Guid Provider, DateOnly Date), ResolvedTier?>();
        var folded = new List<(string? TierCode, bool IsOutOfNetwork, decimal NetQuantity)>(movements.Count);

        foreach (var m in movements)
        {
            var signed = BenefitAccumulation.SignedDelta(m.Direction, m.Quantity);

            if (m.ProviderId is not { } provider || m.ServiceDate is not { } date)
            {
                folded.Add((null, false, signed));   // unattributed — its own bucket, never in-network
                continue;
            }

            var key = (provider, date);
            if (!cache.TryGetValue(key, out var tier))
            {
                tier = await tiers.ResolveAsync(
                    new TierQuery(provider, date, m.ProviderLocationId), bearerToken, ct);
                cache[key] = tier;
            }

            // An unresolvable tier is unattributed, NOT out-of-network. 19.1b's fail-safe deliberately prices
            // an unresolved provider as OON because charging the safer amount protects the member; a REPORT
            // has no such asymmetry, and recording a resolution outage as real out-of-network volume would
            // send the Network Team renegotiating a contract that already exists.
            folded.Add(tier is null ? (null, false, signed) : (tier.TierCode, tier.IsOutOfNetwork, signed));
        }

        return UtilizationMath.SplitByTier(folded);
    }

    /// <summary>
    /// Window-scoped ledger activity per benefit category.
    ///
    /// Deliberately NOT named "consumed": see the header of <c>Utilization.cs</c>. The accumulator resets at
    /// period boundaries and the ledger does not, so over any window spanning a reset these two numbers
    /// differ — and both are right.
    /// </summary>
    public async Task<IReadOnlyList<CategoryActivity>> ActivityAsync(
        IReadOnlyCollection<Guid> beneficiaryIds, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(beneficiaryIds);
        if (beneficiaryIds.Count == 0) return [];

        var ids = beneficiaryIds.ToList();
        var rows = await db.BenefitConsumptions.AsNoTracking()
            .Where(r => ids.Contains(r.BeneficiaryId)
                        && r.ServiceDate != null && r.ServiceDate >= from && r.ServiceDate <= to
                        && r.BenefitCategory != null
                        && (r.Outcome == ConsumptionOutcome.Applied || r.Outcome == ConsumptionOutcome.Reversed))
            .GroupBy(r => new { r.BenefitCategory, r.Direction })
            .Select(g => new { g.Key.BenefitCategory, g.Key.Direction, Quantity = g.Sum(r => r.Quantity), Count = g.Count() })
            .ToListAsync(ct);

        return [.. rows.GroupBy(r => r.BenefitCategory!)
            .Select(g => new CategoryActivity(
                g.Key,
                g.Where(r => r.Direction == ConsumptionDirection.Applied).Sum(r => r.Quantity),
                g.Where(r => r.Direction == ConsumptionDirection.Reversed).Sum(r => r.Quantity),
                g.Sum(r => r.Count)))
            .OrderBy(a => a.BenefitCategoryCode, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The reconciliation check, run as part of the request rather than only in a test.
    ///
    /// Returns the sum of <c>coverage_limit.consumed_value</c> for the scope, straight from SQL and along a
    /// completely different path from <see cref="MemberTotalsAsync"/>. The endpoint compares the two and says
    /// so in the payload: a utilization figure that has not been checked against the accumulator is exactly
    /// the figure someone will use to refuse a refugee's care.
    /// </summary>
    public async Task<decimal> AccumulatorTotalAsync(
        IReadOnlyCollection<Guid> beneficiaryIds, DateOnly asOf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(beneficiaryIds);
        if (beneficiaryIds.Count == 0) return 0m;

        var ids = beneficiaryIds.ToList();
        var accumulating = new[] { LimitType.Annual, LimitType.Lifetime, LimitType.Count };

        return await db.CoverageLimits.AsNoTracking()
            .Where(l => accumulating.Contains(l.LimitType))
            .Join(db.Coverages.AsNoTracking()
                    .Where(c => ids.Contains(c.BeneficiaryId) && !c.IsDeleted
                                && c.Status == CoverageStatus.Active
                                && c.EffectiveFrom <= asOf && (c.EffectiveTo == null || c.EffectiveTo >= asOf)),
                l => l.CoverageId, c => c.CoverageId, (l, _) => l.ConsumedValue)
            .SumAsync(ct);
    }
}
