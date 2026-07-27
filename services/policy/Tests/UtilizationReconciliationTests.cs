using FluentAssertions;
using Mersal.BenefitPricing;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.4 against real Postgres (env-gated <c>POLICY_TEST_DB</c>, migration 0012 applied).
///
/// <b>THE acceptance criterion for the sub-prompt: every response reconciles EXACTLY to the accumulator.</b>
///
/// The build prompt asks for this as a test rather than a comment, and the reason is specific. Utilization is
/// the number Finance renegotiates contracts on and the number a supervisor uses to decide whether a member
/// has anything left. If it drifts from <c>coverage_limit.consumed_value</c> — the value eligibility actually
/// refuses care on — then the report and the counter disagree about the same person on the same day, and the
/// person at the counter is a refugee with no way to appeal a spreadsheet.
///
/// Each test sums along a path INDEPENDENT of the one the report uses, so agreement is evidence rather than
/// tautology.
/// </summary>
public class UtilizationReconciliationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("POLICY_TEST_DB");
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private static readonly DateOnly AsOf = new(2026, 6, 15);

    private static PolicyDbContext Ctx() => new(new DbContextOptionsBuilder<PolicyDbContext>()
        .UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static UtilizationQuery Query(PolicyDbContext db) => new(db, new StubTierResolver());

    // ---- Reconciliation ----------------------------------------------------------------------------------

    [SkippableFact]
    public async Task One_members_reported_consumption_equals_their_accumulator()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = Query(db);

            var categories = await query.MemberAccumulatorsAsync(f.Members[0], AsOf);
            var reported = categories.Sum(c => c.ConsumedValue);
            var accumulator = await query.AccumulatorTotalAsync([f.Members[0]], AsOf);

            reported.Should().Be(accumulator);
            reported.Should().Be(30m, "the seed consumed 30 across this member's limits");
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task A_groups_total_equals_the_sum_of_its_members_accumulators()
    {
        // The aggregation path (per-member totals, rolled) versus the SQL path (SUM over coverage_limit).
        // Two independent routes to one number; the whole read model is only trustworthy if they agree.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = Query(db);

            var members = await query.MembersAsync(UtilizationScope.Group, f.GroupId, includeInactive: false);
            var totals = await query.MemberTotalsAsync(members, AsOf);
            var (_, consumed, _, _) = UtilizationMath.Roll(totals);

            var accumulator = await query.AccumulatorTotalAsync(
                [.. members.Select(m => m.BeneficiaryId)], AsOf);

            consumed.Should().Be(accumulator);
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task Policy_and_payer_scopes_reconcile_the_same_way()
    {
        // A payer is one hop above a policy. If the hop were wrong — a missed join, a soft-deleted policy left
        // in — the payer total would silently exceed the policies it is made of.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = Query(db);

            foreach (var (scope, id) in new[]
            {
                (UtilizationScope.Policy, f.PolicyId),
                (UtilizationScope.Payer, f.PayerId),
                (UtilizationScope.Plan, f.PolicyPlanId),
            })
            {
                var members = await query.MembersAsync(scope, id, includeInactive: false);
                var totals = await query.MemberTotalsAsync(members, AsOf);
                var (_, consumed, _, _) = UtilizationMath.Roll(totals);
                var accumulator = await query.AccumulatorTotalAsync(
                    [.. members.Select(m => m.BeneficiaryId)], AsOf);

                consumed.Should().Be(accumulator, $"the {scope} scope must reconcile");
            }
        }
        finally { await Cleanup(f); }
    }

    // ---- Scope resolution --------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Every_scope_resolves_to_the_members_it_should()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = Query(db);

            (await query.MembersAsync(UtilizationScope.Policy, f.PolicyId, false)).Should().HaveCount(3);
            (await query.MembersAsync(UtilizationScope.Payer, f.PayerId, false)).Should().HaveCount(3);
            (await query.MembersAsync(UtilizationScope.Plan, f.PolicyPlanId, false)).Should().HaveCount(3);
            (await query.MembersAsync(UtilizationScope.Group, f.GroupId, false)).Should().HaveCount(2,
                "the third member is not in the group");
            (await query.MembersAsync(UtilizationScope.Member, f.Members[0], false)).Should().HaveCount(1);
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task A_terminated_membership_is_excluded_from_the_member_list_by_default()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using (var db = Ctx())
            {
                var enrollment = await db.Enrollments.FirstAsync(e => e.BeneficiaryId == f.Members[2]);
                enrollment.Status = EnrollmentStatus.Terminated;
                enrollment.TerminationReason = "test";
                await db.SaveChangesAsync();
            }

            await using var check = Ctx();
            var query = Query(check);

            (await query.MembersAsync(UtilizationScope.Policy, f.PolicyId, includeInactive: false))
                .Should().HaveCount(2);
            (await query.MembersAsync(UtilizationScope.Policy, f.PolicyId, includeInactive: true))
                .Should().HaveCount(3, "their consumption happened and still has to be readable");
        }
        finally { await Cleanup(f); }
    }

    // ---- The accumulator versus the ledger ---------------------------------------------------------------

    [SkippableFact]
    public async Task Window_activity_comes_from_the_ledger_and_is_not_the_accumulator()
    {
        // The distinction the whole read model is built around. coverage_limit RESETS at a period boundary;
        // benefit_consumption does not. Reporting either under the other's name is how a report tells Finance
        // a member is over their limit when they are not.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = Query(db);

            var activity = await query.ActivityAsync([f.Members[0]], new(2026, 1, 1), new(2026, 12, 31));
            activity.Sum(a => a.NetQuantity).Should().Be(40m, "the ledger holds every movement ever made");

            var accumulators = await query.MemberAccumulatorsAsync(f.Members[0], AsOf);
            accumulators.Sum(a => a.ConsumedValue).Should().Be(30m,
                "the accumulator is the current period only — 10 was reversed after a reset");
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task Activity_is_bounded_by_the_service_date_not_by_when_it_was_recorded()
    {
        // applied_at lags the care by however long the broker and any retry took. Windowing on it would move a
        // member's consumption into the wrong month whenever the queue was slow.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = Query(db);

            var march = await query.ActivityAsync([f.Members[0]], new(2026, 3, 1), new(2026, 3, 31));
            march.Sum(a => a.NetQuantity).Should().Be(20m);

            var april = await query.ActivityAsync([f.Members[0]], new(2026, 4, 1), new(2026, 4, 30));
            april.Sum(a => a.NetQuantity).Should().Be(20m);
        }
        finally { await Cleanup(f); }
    }

    // ---- The tier split ----------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Consumption_splits_by_the_tier_in_force_on_the_service_date()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = Query(db);

            var split = await query.TierSplitAsync([f.Members[0]], new(2026, 1, 1), new(2026, 12, 31), null);

            split.Should().Contain(t => t.TierCode == "T1" && t.IsAttributed);
            split.Sum(t => t.NetQuantity).Should().Be(40m, "the split partitions the ledger, it does not filter it");
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task A_movement_with_no_provider_lands_in_the_unattributed_bucket()
    {
        // Rows written before 0012 have no provider. They must be visibly unattributed rather than quietly
        // in-network — understating out-of-network flatters the network on the number it is judged by.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = Query(db);

            var split = await query.TierSplitAsync([f.Members[1]], new(2026, 1, 1), new(2026, 12, 31), null);

            var unattributed = split.Single(t => t.TierCode == TierUtilization.UnattributedCode);
            unattributed.IsAttributed.Should().BeFalse();
            unattributed.IsOutOfNetwork.Should().BeFalse();
            unattributed.NetQuantity.Should().Be(15m);
        }
        finally { await Cleanup(f); }
    }

    // ---- Seed / teardown ---------------------------------------------------------------------------------

    private sealed record Fixture(
        Guid PayerId, Guid PolicyId, Guid PlanId, Guid PlanVersionId, Guid PolicyPlanId, Guid GroupId,
        IReadOnlyList<Guid> Members, Guid ProviderId, Guid CategoryId);

    /// <summary>
    /// Three members on one policy/plan, two of them in a group.
    ///
    /// Member 0: an Annual limit of 100 with 30 consumed, and four ledger movements (two in March at a known
    /// provider, two in April) netting 40 — deliberately NOT equal to the accumulator, so the accumulator-vs-
    /// ledger distinction is exercised rather than assumed.
    /// Member 1: 15 consumed, with NO provider on the ledger rows (the pre-0012 shape).
    /// Member 2: a member with coverage and no consumption at all.
    /// </summary>
    private static async Task<Fixture> Seed()
    {
        await using var db = Ctx();

        var category = await db.BenefitCategories.FirstOrDefaultAsync(c => c.Code == "LAB");
        if (category is null)
        {
            category = new BenefitCategory
            {
                BenefitCategoryId = Guid.NewGuid(), TenantId = Tenant, Code = "LAB", Name = "Laboratory",
            };
            db.BenefitCategories.Add(category);
            await db.SaveChangesAsync();
        }

        var payer = new Payer
        {
            PayerId = Guid.NewGuid(), TenantId = Tenant, PayerCode = $"PY{Guid.NewGuid():N}"[..10],
            NameEn = "Util Test Payer", NameAr = "Util Test Payer", PayerType = PayerType.Donor,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var plan = new Plan
        {
            PlanId = Guid.NewGuid(), TenantId = Tenant, PlanCode = $"P{Guid.NewGuid():N}"[..12],
            NameEn = "Util", NameAr = "Util", Category = "Primary",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var version = new PlanVersion
        {
            PlanVersionId = Guid.NewGuid(), TenantId = Tenant, PlanId = plan.PlanId, VersionNo = 1,
            EffectiveFrom = new DateOnly(2026, 1, 1), Status = PlanVersionStatus.Active,
            ActivatedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var policy = new Domain.Policy
        {
            PolicyId = Guid.NewGuid(), TenantId = Tenant, PolicyNo = $"POL-{Guid.NewGuid():N}"[..20],
            PayerId = payer.PayerId,
            EffectiveFrom = new DateOnly(2026, 1, 1), Status = PolicyStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var policyPlan = new PolicyPlan
        {
            PolicyPlanId = Guid.NewGuid(), TenantId = Tenant, PolicyId = policy.PolicyId,
            PlanVersionId = version.PlanVersionId, PlanLabel = "Standard",
            EffectiveFrom = new DateOnly(2026, 1, 1), IsDefault = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var group = new MemberGroup
        {
            GroupId = Guid.NewGuid(), TenantId = Tenant, PolicyId = policy.PolicyId,
            GroupCode = $"G{Guid.NewGuid():N}"[..10], NameEn = "Cohort", NameAr = "Cohort",
            GroupType = MemberGroupType.Cohort, EffectiveFrom = new DateOnly(2026, 1, 1),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };

        // Saved in dependency order. The model declares no navigations between these aggregates (cross-entity
        // FKs are DB-level only), so EF has no graph to order the inserts by and would otherwise pick one.
        db.Payers.Add(payer);
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        db.PlanVersions.Add(version);
        db.Policies.Add(policy);
        await db.SaveChangesAsync();
        db.PolicyPlans.Add(policyPlan);
        db.MemberGroups.Add(group);
        await db.SaveChangesAsync();

        var members = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var provider = Guid.NewGuid();
        decimal[] consumed = [30m, 15m, 0m];

        for (var i = 0; i < members.Length; i++)
        {
            db.Enrollments.Add(new Enrollment
            {
                EnrollmentId = Guid.NewGuid(), TenantId = Tenant, BeneficiaryId = members[i],
                PolicyId = policy.PolicyId, PolicyPlanId = policyPlan.PolicyPlanId,
                GroupId = i < 2 ? group.GroupId : null,
                MemberNo = $"MEM-UTIL-{i}-{Guid.NewGuid():N}"[..24],
                Relationship = Relationship.Principal, EffectiveFrom = new DateOnly(2026, 1, 1),
                Status = EnrollmentStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });

            db.Coverages.Add(new Coverage
            {
                CoverageId = Guid.NewGuid(), TenantId = Tenant, PolicyId = policy.PolicyId,
                BeneficiaryId = members[i], BenefitCategoryId = category.BenefitCategoryId,
                EffectiveFrom = new DateOnly(2026, 1, 1), Status = CoverageStatus.Active,
                Limits =
                [
                    new CoverageLimit
                    {
                        CoverageLimitId = Guid.NewGuid(), TenantId = Tenant,
                        LimitType = LimitType.Annual, LimitValue = 100m, ConsumedValue = consumed[i],
                        CurrencyCode = "EGP", ResetPeriod = ResetPeriod.Yearly,
                        LastResetOn = new DateOnly(2026, 1, 1),
                    },
                ],
            });
        }
        await db.SaveChangesAsync();

        // Member 0's ledger: attributed to a provider, two service dates, netting 40 against an accumulator
        // of 30 — the divergence a reset produces in real life.
        db.BenefitConsumptions.AddRange(
            Ledger(members[0], provider, new DateOnly(2026, 3, 10), 12m, ConsumptionDirection.Applied),
            Ledger(members[0], provider, new DateOnly(2026, 3, 20), 8m, ConsumptionDirection.Applied),
            Ledger(members[0], provider, new DateOnly(2026, 4, 5), 25m, ConsumptionDirection.Applied),
            Ledger(members[0], provider, new DateOnly(2026, 4, 12), 5m, ConsumptionDirection.Reversed),
            // Member 1: the pre-0012 shape — a movement with no provider at all.
            Ledger(members[1], null, new DateOnly(2026, 3, 15), 15m, ConsumptionDirection.Applied));
        await db.SaveChangesAsync();

        return new Fixture(payer.PayerId, policy.PolicyId, plan.PlanId, version.PlanVersionId,
            policyPlan.PolicyPlanId, group.GroupId, members, provider, category.BenefitCategoryId);
    }

    private static BenefitConsumptionRecord Ledger(
        Guid beneficiary, Guid? provider, DateOnly serviceDate, decimal quantity, ConsumptionDirection direction) =>
        new()
        {
            ConsumptionId = Guid.NewGuid(), TenantId = Tenant, EventId = Guid.NewGuid(),
            EventType = "OrderLinesConsumed", SourceRef = $"util-test|{Guid.NewGuid():N}",
            BeneficiaryId = beneficiary, BenefitCategory = "LAB", Quantity = quantity, Direction = direction,
            Outcome = direction == ConsumptionDirection.Applied
                ? ConsumptionOutcome.Applied : ConsumptionOutcome.Reversed,
            MovedLimits = 1, AppliedAt = DateTimeOffset.UtcNow,
            ProviderId = provider, ServiceDate = serviceDate,
        };

    private static async Task Cleanup(Fixture f)
    {
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.plan_version DISABLE TRIGGER trg_plan_version_immutable");
        try
        {
            var ids = f.Members.ToArray();
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.benefit_consumption WHERE beneficiary_id = ANY({0})", [ids]);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.coverage_limit WHERE coverage_id IN " +
                "(SELECT coverage_id FROM policy.coverage WHERE policy_id = {0})", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.coverage WHERE policy_id = {0}", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.enrollment WHERE policy_id = {0}", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.member_group WHERE policy_id = {0}", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.policy_plan WHERE policy_id = {0}", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.policy WHERE policy_id = {0}", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.plan_version WHERE plan_id = {0}", f.PlanId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.plan WHERE plan_id = {0}", f.PlanId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.payer WHERE payer_id = {0}", f.PayerId);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.plan_version ENABLE TRIGGER trg_plan_version_immutable");
        }
    }

    /// <summary>Resolves everything to T1 without reaching provider-service. The resolver's own correctness is
    /// 19.1b's concern and is tested there; what matters here is that utilization ASKS at the service date and
    /// keeps an unresolvable movement out of the in-network bucket.</summary>
    private sealed class StubTierResolver : INetworkTierResolver
    {
        public Task<ResolvedTier?> ResolveAsync(TierQuery query, string? bearerToken, CancellationToken ct = default) =>
            Task.FromResult<ResolvedTier?>(
                new ResolvedTier(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "T1", false, "Provider"));
    }
}
