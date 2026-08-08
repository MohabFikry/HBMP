using Mersal.BenefitPricing;
using Mersal.Authz;
using Mersal.Policy.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Infrastructure;

// Phase 19.5 — policy query and member query (design 38 §4.4).
//
// ============================================================================================================
// PAYER SCOPE IS A PREDICATE HERE, NOT A CHECK AT THE EDGE
// ============================================================================================================
// Every list in this file applies the caller's payer restriction inside the SQL that builds the page. That is
// deliberate: a filter applied after the fact has to be remembered on every new query, on every new sort, and
// on the count that says how many rows exist. The count is the one people forget — and a total of 4 000 beside
// a page of 25 rows tells a payer-restricted user exactly how large another payer's book of business is, which
// is the fact the restriction existed to withhold.
//
// A TARGETED read of one out-of-scope entity is a 403, not an empty result. See PayerScopeRules for why that
// inversion of the usual "don't confirm existence" advice is right for an organisation rather than a person.

/// <summary>One policy as the query returns it, with the aggregates the filters band on.</summary>
public sealed record PolicyQueryRow(
    Guid PolicyId, string PolicyNo, Guid? PayerId, PolicyStatus Status,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, int? MaxMembers,
    int MemberCount, int PlanCount, decimal TotalLimit, decimal TotalConsumed, bool AnyUnlimited)
{
    public MemberCountBand CountBand => MemberCountBands.Of(MemberCount);

    public UtilizationBand Band =>
        UtilizationBands.Of(TotalLimit, TotalConsumed, hasCoverage: MemberCount > 0);

    public decimal? PercentUsed => UtilizationBands.PercentUsed(TotalLimit, TotalConsumed);
}

/// <summary>One membership as the query returns it. Carries NO name — names live in patient-service and are
/// resolved for the page only, through the owner, so a 40 000-row filter never becomes a 40 000-name
/// disclosure.</summary>
public sealed record MemberQueryRow(
    Guid EnrollmentId, Guid BeneficiaryId, string MemberNo, Guid PolicyId, Guid PolicyPlanId, string? PlanLabel,
    Guid? GroupId, Guid? PayerId, Relationship Relationship, EnrollmentStatus Status,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, DateOnly? WaitingPeriodEndsOn, Guid? BranchId,
    string? TerminationReason, decimal TotalLimit, decimal TotalConsumed, bool HasCoverage)
{
    public UtilizationBand Band => UtilizationBands.Of(TotalLimit, TotalConsumed, HasCoverage);

    public decimal? PercentUsed => UtilizationBands.PercentUsed(TotalLimit, TotalConsumed);

    public WaitingPeriodState WaitingPeriod(DateOnly asOf) =>
        WaitingPeriodEndsOn is not { } ends ? WaitingPeriodState.None
        : asOf <= ends ? WaitingPeriodState.Serving
        : WaitingPeriodState.Served;
}

public sealed class AdministrativeQuery(PolicyDbContext db)
{
    /// <summary>The limit types that accumulate toward a ceiling — the same set the 19.4 reconciliation sums,
    /// so a member's band here cannot disagree with their utilization report.</summary>
    private static readonly LimitType[] Accumulating = [LimitType.Annual, LimitType.Lifetime, LimitType.Count];

    // ---- Policy query ------------------------------------------------------------------------------------

    /// <summary>
    /// Structured policy search.
    ///
    /// <para>Aggregates (member count, consumed vs limit) are materialised for the FILTERED set rather than
    /// pushed into the paged SQL. A policy is a contract with a payer: Mersal has tens to hundreds of them, not
    /// millions, and the two band filters are unusable if they run after pagination. Member query, where the
    /// row count genuinely is large, does the opposite — see below.</para>
    /// </summary>
    public async Task<PagedResult<PolicyQueryRow>> PolicyQueryAsync(
        PolicyQueryFilter filter, PageRequest page, SortRequest sort, PermittedPayers payers,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(sort);
        ArgumentNullException.ThrowIfNull(payers);

        var q = db.Policies.AsNoTracking().Where(p => !p.IsDeleted);

        // Payer scope FIRST, so nothing downstream can widen it back.
        if (!payers.IsUnrestricted)
        {
            var ids = payers.PayerIds.ToList();
            q = q.Where(p => p.PayerId != null && ids.Contains(p.PayerId.Value));
        }

        if (filter.PayerId is { } payer) q = q.Where(p => p.PayerId == payer);
        if (filter.Status is { } status) q = q.Where(p => p.Status == status);
        if (!string.IsNullOrWhiteSpace(filter.PolicyNo))
            q = q.Where(p => EF.Functions.ILike(p.PolicyNo, $"%{filter.PolicyNo}%"));
        if (filter.EffectiveOn is { } on)
            q = q.Where(p => p.EffectiveFrom <= on && (p.EffectiveTo == null || p.EffectiveTo >= on));
        if (filter.EffectiveFromAfter is { } from) q = q.Where(p => p.EffectiveFrom >= from);
        if (filter.EffectiveToBefore is { } to) q = q.Where(p => p.EffectiveTo != null && p.EffectiveTo <= to);

        if (filter.GroupId is { } group)
            q = q.Where(p => db.MemberGroups.Any(g => g.GroupId == group && g.PolicyId == p.PolicyId && !g.IsDeleted));

        // Plan and plan LABEL are separate filters on purpose: "every policy offering a plan called Oncology"
        // is a question about the label, and labels repeat across policies by design (19.2b).
        if (filter.PlanId is { } planId)
            q = q.Where(p => db.PolicyPlans.Any(pp => pp.PolicyId == p.PolicyId && !pp.IsDeleted
                && db.PlanVersions.Any(v => v.PlanVersionId == pp.PlanVersionId && v.PlanId == planId)));
        if (!string.IsNullOrWhiteSpace(filter.PlanLabel))
            q = q.Where(p => db.PolicyPlans.Any(pp => pp.PolicyId == p.PolicyId && !pp.IsDeleted
                && EF.Functions.ILike(pp.PlanLabel, $"%{filter.PlanLabel}%")));

        var policies = await q.Select(p => new
        {
            p.PolicyId, p.PolicyNo, p.PayerId, p.Status, p.EffectiveFrom, p.EffectiveTo, p.MaxMembers,
        }).ToListAsync(ct);

        if (policies.Count == 0)
            return new PagedResult<PolicyQueryRow>([], page.Page, page.PageSize, 0);

        var policyIds = policies.ConvertAll(p => p.PolicyId);

        var memberCounts = await db.Enrollments.AsNoTracking()
            .Where(e => policyIds.Contains(e.PolicyId) && !e.IsDeleted
                        && (e.Status == EnrollmentStatus.Active || e.Status == EnrollmentStatus.Suspended))
            .GroupBy(e => e.PolicyId)
            .Select(g => new { PolicyId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PolicyId, x => x.Count, ct);

        var planCounts = await db.PolicyPlans.AsNoTracking()
            .Where(pp => policyIds.Contains(pp.PolicyId) && !pp.IsDeleted)
            .GroupBy(pp => pp.PolicyId)
            .Select(g => new { PolicyId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PolicyId, x => x.Count, ct);

        // The accumulator, rolled up per policy along the same coverage→coverage_limit path 19.4 reconciles on.
        var accumulators = await db.CoverageLimits.AsNoTracking()
            .Where(l => Accumulating.Contains(l.LimitType))
            .Join(db.Coverages.AsNoTracking().Where(c => policyIds.Contains(c.PolicyId) && !c.IsDeleted
                        && c.Status == CoverageStatus.Active),
                l => l.CoverageId, c => c.CoverageId, (l, c) => new { c.PolicyId, l.LimitValue, l.ConsumedValue })
            .GroupBy(x => x.PolicyId)
            .Select(g => new
            {
                PolicyId = g.Key,
                Limit = g.Sum(x => x.LimitValue),
                Consumed = g.Sum(x => x.ConsumedValue),
            })
            .ToDictionaryAsync(x => x.PolicyId, x => (x.Limit, x.Consumed), ct);

        var rows = policies.ConvertAll(p =>
        {
            var (limit, consumed) = accumulators.TryGetValue(p.PolicyId, out var a) ? a : (0m, 0m);
            return new PolicyQueryRow(
                p.PolicyId, p.PolicyNo, p.PayerId, p.Status, p.EffectiveFrom, p.EffectiveTo, p.MaxMembers,
                memberCounts.GetValueOrDefault(p.PolicyId), planCounts.GetValueOrDefault(p.PolicyId),
                limit, consumed, AnyUnlimited: limit <= 0m && memberCounts.GetValueOrDefault(p.PolicyId) > 0);
        });

        if (filter.MemberCountBand is { } countBand)
            rows = rows.FindAll(r => r.CountBand == countBand);
        if (filter.UtilizationBand is { } band)
            rows = rows.FindAll(r => r.Band == band);

        var total = rows.Count;
        var ordered = Sort(rows, sort);
        return new PagedResult<PolicyQueryRow>(
            [.. ordered.Skip(page.Skip).Take(page.PageSize)], page.Page, page.PageSize, total);
    }

    private static IEnumerable<PolicyQueryRow> Sort(List<PolicyQueryRow> rows, SortRequest sort)
    {
        IOrderedEnumerable<PolicyQueryRow> ordered = sort.Field switch
        {
            "effectivefrom" => rows.OrderBy(r => r.EffectiveFrom),
            "effectiveto" => rows.OrderBy(r => r.EffectiveTo ?? DateOnly.MaxValue),
            "status" => rows.OrderBy(r => r.Status.ToString(), StringComparer.Ordinal),
            "membercount" => rows.OrderBy(r => r.MemberCount),
            "percentused" => rows.OrderBy(r => r.PercentUsed ?? -1m),
            _ => rows.OrderBy(r => r.PolicyNo, StringComparer.Ordinal),
        };
        // A stable tiebreak on the business key. Without it two pages of an equal-valued sort can repeat or
        // skip a row, and a caller paging through 40 policies would never know which.
        ordered = ordered.ThenBy(r => r.PolicyNo, StringComparer.Ordinal);
        return sort.Descending ? ordered.Reverse() : ordered;
    }

    // ---- Member query ------------------------------------------------------------------------------------

    /// <summary>
    /// Structured member search.
    ///
    /// <para>Unlike policy query this stays in SQL end to end — filter, band, sort and page — because the row
    /// count here is the membership, and materialising it to band in memory would pull an entire policy's
    /// enrolment into the service to return twenty-five rows.</para>
    ///
    /// <para>The two accumulator sums are correlated subqueries evaluated per candidate row. That is the cost
    /// of making "who is over 80% of their limit" answerable without a maintained projection — and it is the
    /// same trade ADR-0023 made for utilization: a number that cannot drift, computed each time it is asked
    /// for.</para>
    /// </summary>
    public async Task<PagedResult<MemberQueryRow>> MemberQueryAsync(
        MemberQueryFilter filter, PageRequest page, SortRequest sort, PermittedPayers payers, DateOnly asOf,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(sort);
        ArgumentNullException.ThrowIfNull(payers);

        var q = db.Enrollments.AsNoTracking().Where(e => !e.IsDeleted);

        if (!payers.IsUnrestricted)
        {
            var ids = payers.PayerIds.ToList();
            q = q.Where(e => db.Policies.Any(p => p.PolicyId == e.PolicyId && !p.IsDeleted
                                                  && p.PayerId != null && ids.Contains(p.PayerId.Value)));
        }

        if (filter.PolicyId is { } policyId) q = q.Where(e => e.PolicyId == policyId);
        if (filter.PolicyPlanId is { } planId) q = q.Where(e => e.PolicyPlanId == planId);
        if (filter.GroupId is { } groupId) q = q.Where(e => e.GroupId == groupId);
        if (filter.Relationship is { } rel) q = q.Where(e => e.Relationship == rel);
        if (filter.Status is { } status) q = q.Where(e => e.Status == status);
        if (!string.IsNullOrWhiteSpace(filter.MemberNo))
            q = q.Where(e => EF.Functions.ILike(e.MemberNo, $"%{filter.MemberNo}%"));

        // A specific branch EXCLUDES unattributed rows: "members enrolled at Maadi" is a question a NULL
        // genuinely does not answer. Branch NARROWING (a branch-scoped caller with no explicit filter) is a
        // different decision and is applied by the endpoint — see 0013's header.
        if (filter.BranchId is { } branch) q = q.Where(e => e.BranchId == branch);

        if (filter.EnrolledOn is { } on)
            q = q.Where(e => e.EffectiveFrom <= on && (e.EffectiveTo == null || e.EffectiveTo >= on));
        if (filter.EnrolledFromAfter is { } from) q = q.Where(e => e.EffectiveFrom >= from);
        if (filter.EnrolledToBefore is { } to) q = q.Where(e => e.EffectiveTo != null && e.EffectiveTo <= to);

        q = filter.WaitingPeriod switch
        {
            WaitingPeriodState.None => q.Where(e => e.WaitingPeriodEndsOn == null),
            WaitingPeriodState.Serving => q.Where(e => e.WaitingPeriodEndsOn != null && asOf <= e.WaitingPeriodEndsOn),
            WaitingPeriodState.Served => q.Where(e => e.WaitingPeriodEndsOn != null && asOf > e.WaitingPeriodEndsOn),
            _ => q,
        };

        // Identity filters are resolved by patient-service BEFORE we get here. An empty (not null) list means
        // "the name/identifier matched nobody" and must return no rows — treating it as "no filter" would
        // answer a failed lookup with the entire membership.
        if (filter.BeneficiaryIds is { } beneficiaries)
        {
            if (beneficiaries.Count == 0)
                return new PagedResult<MemberQueryRow>([], page.Page, page.PageSize, 0);
            var list = beneficiaries.ToList();
            q = q.Where(e => list.Contains(e.BeneficiaryId));
        }

        var projected = q.Select(e => new
        {
            Enrollment = e,
            PlanLabel = db.PolicyPlans.Where(pp => pp.PolicyPlanId == e.PolicyPlanId)
                          .Select(pp => pp.PlanLabel).FirstOrDefault(),
            PayerId = db.Policies.Where(p => p.PolicyId == e.PolicyId).Select(p => p.PayerId).FirstOrDefault(),
            Limit = db.CoverageLimits
                .Where(l => Accumulating.Contains(l.LimitType) && db.Coverages.Any(c =>
                    c.CoverageId == l.CoverageId && c.BeneficiaryId == e.BeneficiaryId && !c.IsDeleted
                    && c.Status == CoverageStatus.Active))
                .Sum(l => (decimal?)l.LimitValue) ?? 0m,
            Consumed = db.CoverageLimits
                .Where(l => Accumulating.Contains(l.LimitType) && db.Coverages.Any(c =>
                    c.CoverageId == l.CoverageId && c.BeneficiaryId == e.BeneficiaryId && !c.IsDeleted
                    && c.Status == CoverageStatus.Active))
                .Sum(l => (decimal?)l.ConsumedValue) ?? 0m,
            HasCoverage = db.Coverages.Any(c => c.BeneficiaryId == e.BeneficiaryId && !c.IsDeleted
                                                && c.Status == CoverageStatus.Active),
        });

        projected = filter.UtilizationBand switch
        {
            UtilizationBand.Zero => projected.Where(x =>
                (x.Limit <= 0m && !x.HasCoverage) || (x.Limit > 0m && x.Consumed <= 0m)),
            UtilizationBand.Unlimited => projected.Where(x => x.Limit <= 0m && x.HasCoverage),
            UtilizationBand.Exhausted => projected.Where(x => x.Limit > 0m && x.Consumed >= x.Limit),
            UtilizationBand.High => projected.Where(x =>
                x.Limit > 0m && x.Consumed < x.Limit && x.Consumed * 100m >= 80m * x.Limit),
            UtilizationBand.Medium => projected.Where(x =>
                x.Limit > 0m && x.Consumed * 100m >= 50m * x.Limit && x.Consumed * 100m < 80m * x.Limit),
            UtilizationBand.Low => projected.Where(x =>
                x.Limit > 0m && x.Consumed > 0m && x.Consumed * 100m < 50m * x.Limit),
            _ => projected,
        };

        var total = await projected.CountAsync(ct);

        // Percent is a guarded ratio: an unguarded consumed/limit divides by zero on every unlimited member,
        // and Postgres raises rather than returning null.
        var ordered = sort.Field switch
        {
            "effectivefrom" => sort.Descending
                ? projected.OrderByDescending(x => x.Enrollment.EffectiveFrom)
                : projected.OrderBy(x => x.Enrollment.EffectiveFrom),
            "effectiveto" => sort.Descending
                ? projected.OrderByDescending(x => x.Enrollment.EffectiveTo)
                : projected.OrderBy(x => x.Enrollment.EffectiveTo),
            "status" => sort.Descending
                ? projected.OrderByDescending(x => x.Enrollment.Status)
                : projected.OrderBy(x => x.Enrollment.Status),
            "relationship" => sort.Descending
                ? projected.OrderByDescending(x => x.Enrollment.Relationship)
                : projected.OrderBy(x => x.Enrollment.Relationship),
            "consumed" => sort.Descending
                ? projected.OrderByDescending(x => x.Consumed)
                : projected.OrderBy(x => x.Consumed),
            "percentused" => sort.Descending
                ? projected.OrderByDescending(x => x.Limit > 0m ? x.Consumed / x.Limit : 0m)
                : projected.OrderBy(x => x.Limit > 0m ? x.Consumed / x.Limit : 0m),
            _ => sort.Descending
                ? projected.OrderByDescending(x => x.Enrollment.MemberNo)
                : projected.OrderBy(x => x.Enrollment.MemberNo),
        };

        var items = await ordered
            .ThenBy(x => x.Enrollment.MemberNo)   // stable tiebreak — see Sort() above
            .Skip(page.Skip).Take(page.PageSize)
            .ToListAsync(ct);

        var rows = items.ConvertAll(x => new MemberQueryRow(
            x.Enrollment.EnrollmentId, x.Enrollment.BeneficiaryId, x.Enrollment.MemberNo, x.Enrollment.PolicyId,
            x.Enrollment.PolicyPlanId, x.PlanLabel, x.Enrollment.GroupId, x.PayerId,
            x.Enrollment.Relationship, x.Enrollment.Status, x.Enrollment.EffectiveFrom, x.Enrollment.EffectiveTo,
            x.Enrollment.WaitingPeriodEndsOn, x.Enrollment.BranchId, x.Enrollment.TerminationReason,
            x.Limit, x.Consumed, x.HasCoverage));

        return new PagedResult<MemberQueryRow>(rows, page.Page, page.PageSize, total);
    }

    // ---- Targeted lookups (payer scope resolves to 403, not to an empty result) --------------------------

    /// <summary>The payer a policy belongs to, or null when the policy does not exist. The endpoint tells the
    /// two apart: a missing policy is 404, an out-of-scope one is 403.</summary>
    public async Task<(bool Exists, Guid? PayerId)> PolicyPayerAsync(Guid policyId, CancellationToken ct = default)
    {
        var row = await db.Policies.AsNoTracking()
            .Where(p => p.PolicyId == policyId && !p.IsDeleted)
            .Select(p => new { p.PayerId })
            .FirstOrDefaultAsync(ct);
        return row is null ? (false, null) : (true, row.PayerId);
    }

    /// <summary>The payer behind an enrolment, for the same purpose.</summary>
    public async Task<(bool Exists, Guid? PayerId)> EnrollmentPayerAsync(Guid enrollmentId, CancellationToken ct = default)
    {
        var row = await db.Enrollments.AsNoTracking()
            .Where(e => e.EnrollmentId == enrollmentId && !e.IsDeleted)
            .Select(e => new { PayerId = db.Policies.Where(p => p.PolicyId == e.PolicyId).Select(p => p.PayerId).FirstOrDefault() })
            .FirstOrDefaultAsync(ct);
        return row is null ? (false, null) : (true, row.PayerId);
    }

    /// <summary>Every payer behind a beneficiary's memberships. A beneficiary can be enrolled under more than
    /// one policy; the 360 is readable when the caller may see AT LEAST ONE of them, and the sections they may
    /// not see are omitted rather than the whole record refused.</summary>
    public async Task<IReadOnlyList<Guid?>> BeneficiaryPayersAsync(Guid beneficiaryId, CancellationToken ct = default) =>
        await db.Enrollments.AsNoTracking()
            .Where(e => e.BeneficiaryId == beneficiaryId && !e.IsDeleted)
            .Select(e => db.Policies.Where(p => p.PolicyId == e.PolicyId).Select(p => p.PayerId).FirstOrDefault())
            .Distinct()
            .ToListAsync(ct);

    // ---- The covered household ---------------------------------------------------------------------------

    /// <summary>One enrolment's place in the graph, enough to root its household without loading the row.</summary>
    public async Task<(bool Exists, Guid BeneficiaryId, Guid Root, Guid? PayerId)> EnrollmentHouseholdRootAsync(
        Guid enrollmentId, CancellationToken ct = default)
    {
        var row = await db.Enrollments.AsNoTracking()
            .Where(e => e.EnrollmentId == enrollmentId && !e.IsDeleted)
            .Select(e => new
            {
                e.EnrollmentId,
                e.BeneficiaryId,
                e.PrincipalEnrollmentId,
                PayerId = db.Policies.Where(p => p.PolicyId == e.PolicyId).Select(p => p.PayerId).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? (false, Guid.Empty, Guid.Empty, null)
            : (true, row.BeneficiaryId, Household.RootOf(row.EnrollmentId, row.PrincipalEnrollmentId), row.PayerId);
    }

    /// <summary>
    /// Everyone enrolled under the given household roots — the principals themselves and every dependant
    /// pointing at them.
    ///
    /// <para>The ROOTS are the argument rather than "the enrolments I already have", which is what makes this
    /// symmetric from any member of the family; see <see cref="Household"/>. The caller decides whether to
    /// subtract the person who asked.</para>
    ///
    /// <para>A terminated dependant stays in the result. "Who else is on this cover" is asked to understand a
    /// family's history as often as its present, and a child whose cover ended last month is the answer to why
    /// their claim was rejected — the status rides along so the reader can tell.</para>
    /// </summary>
    public async Task<IReadOnlyList<Enrollment>> HouseholdAsync(
        IReadOnlyCollection<Guid> roots, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0) return [];

        var ids = roots.ToList();
        var members = await db.Enrollments.AsNoTracking()
            .Where(e => !e.IsDeleted
                        && (ids.Contains(e.EnrollmentId)
                            || (e.PrincipalEnrollmentId != null && ids.Contains(e.PrincipalEnrollmentId.Value))))
            .ToListAsync(ct);

        // Ordered in memory: a household is a handful of rows, and the order is a display rule
        // (Household.SortKey) rather than something to re-express as SQL that could drift from it.
        return [.. members
            .OrderBy(e => Household.SortKey(e.PrincipalEnrollmentId is null, e.Relationship))
            .ThenBy(e => e.MemberNo, StringComparer.Ordinal)];
    }
}
