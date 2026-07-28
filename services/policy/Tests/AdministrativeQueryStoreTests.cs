using Mersal.BenefitPricing;
using FluentAssertions;
using Mersal.Authz;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.5 against real Postgres (env-gated <c>POLICY_TEST_DB</c>, migration 0013 applied).
///
/// <b>The acceptance criterion these exist for: a payer-restricted caller sees ONLY their payer — including in
/// the row count.</b> The count is the one people forget. A total of 4 000 beside a page of 25 tells a
/// restricted user exactly how large another payer's book of business is, which is the fact the restriction
/// existed to withhold.
///
/// The band, sort and pagination tests run against SQL rather than a list because that is where they actually
/// execute: member query filters, bands, sorts and pages entirely in the database, and a band predicate that
/// is right in C# and wrong in SQL is right nowhere that matters.
/// </summary>
[Collection("policy-db")]
public class AdministrativeQueryStoreTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("POLICY_TEST_DB");
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private static readonly DateOnly AsOf = new(2026, 7, 1);

    private static PolicyDbContext Ctx() => new(new DbContextOptionsBuilder<PolicyDbContext>()
        .UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static readonly PageRequest FirstPage = PageRequest.Of(1, 50);
    private static readonly SortRequest ByMemberNo = new(MemberSortFields.Default, false);
    private static readonly SortRequest ByPolicyNo = new(PolicySortFields.Default, false);

    // ---- Payer scope -------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_payer_restricted_caller_sees_only_their_payers_policies()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = new AdministrativeQuery(db);

            var mine = await query.PolicyQueryAsync(
                new PolicyQueryFilter(PolicyNo: f.Prefix), FirstPage, ByPolicyNo,
                PermittedPayers.RestrictedTo([f.PayerA]));

            mine.Items.Should().OnlyContain(p => p.PayerId == f.PayerA);
            mine.Items.Should().HaveCount(1);
            // THE POINT: the total is narrowed too. A count that leaks is a count that describes somebody
            // else's book of business.
            mine.TotalCount.Should().Be(1);

            var unrestricted = await query.PolicyQueryAsync(
                new PolicyQueryFilter(PolicyNo: f.Prefix), FirstPage, ByPolicyNo, PermittedPayers.Unrestricted);
            unrestricted.TotalCount.Should().Be(2);
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task A_payer_restricted_caller_sees_no_members_of_another_payers_policy()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = new AdministrativeQuery(db);

            var page = await query.MemberQueryAsync(
                new MemberQueryFilter(MemberNo: f.Prefix), FirstPage, ByMemberNo,
                PermittedPayers.RestrictedTo([f.PayerA]), AsOf);

            page.Items.Should().OnlyContain(m => m.PolicyId == f.PolicyA);
            page.TotalCount.Should().Be(3, "policy A has three members; policy B's are outside this caller's scope");
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task A_targeted_read_of_another_payers_policy_is_answerable_as_a_denial_not_as_absence()
    {
        // The endpoint turns this into 403 rather than 404/empty. The query's job is to say the policy EXISTS
        // and whose it is; conflating "no such policy" with "not yours" sends an administrator looking straight
        // at the policy number to raise a data-loss incident over a permission setting.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = new AdministrativeQuery(db);

            var (exists, payer) = await query.PolicyPayerAsync(f.PolicyB);
            exists.Should().BeTrue();
            PayerScopeRules.Check(PermittedPayers.RestrictedTo([f.PayerA]), payer)
                .Should().Be(PayerScopeOutcome.Denied);

            var (missing, _) = await query.PolicyPayerAsync(Guid.NewGuid());
            missing.Should().BeFalse("a policy that does not exist is a different answer entirely");
        }
        finally { await Cleanup(f); }
    }

    // ---- Filters -----------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task The_utilization_band_filter_runs_in_sql_and_agrees_with_the_domain_rule()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = new AdministrativeQuery(db);

            var high = await query.MemberQueryAsync(
                new MemberQueryFilter(MemberNo: f.Prefix, UtilizationBand: UtilizationBand.High),
                FirstPage, ByMemberNo, PermittedPayers.Unrestricted, AsOf);
            high.Items.Should().OnlyContain(m => m.Band == UtilizationBand.High);
            high.TotalCount.Should().Be(1, "one seeded member sits at 85 of 100");

            var zero = await query.MemberQueryAsync(
                new MemberQueryFilter(MemberNo: f.Prefix, UtilizationBand: UtilizationBand.Zero),
                FirstPage, ByMemberNo, PermittedPayers.Unrestricted, AsOf);
            zero.Items.Should().OnlyContain(m => m.Band == UtilizationBand.Zero);

            var exhausted = await query.MemberQueryAsync(
                new MemberQueryFilter(MemberNo: f.Prefix, UtilizationBand: UtilizationBand.Exhausted),
                FirstPage, ByMemberNo, PermittedPayers.Unrestricted, AsOf);
            exhausted.TotalCount.Should().Be(1, "one seeded member is over their ceiling");
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task The_waiting_period_filter_treats_the_boundary_day_as_still_serving()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = new AdministrativeQuery(db);

            // The seeded waiting period ends 2026-08-31; on that day the member is STILL serving it.
            var serving = await query.MemberQueryAsync(
                new MemberQueryFilter(MemberNo: f.Prefix, WaitingPeriod: WaitingPeriodState.Serving),
                FirstPage, ByMemberNo, PermittedPayers.Unrestricted, new DateOnly(2026, 8, 31));
            serving.TotalCount.Should().Be(1);

            var served = await query.MemberQueryAsync(
                new MemberQueryFilter(MemberNo: f.Prefix, WaitingPeriod: WaitingPeriodState.Serving),
                FirstPage, ByMemberNo, PermittedPayers.Unrestricted, new DateOnly(2026, 9, 1));
            served.TotalCount.Should().Be(0, "the day after the boundary the member is in benefit");
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task A_named_branch_excludes_the_members_whose_branch_was_never_recorded()
    {
        // "Members enrolled at Maadi" is a question a NULL genuinely does not answer. Branch NARROWING for a
        // branch-scoped caller is the opposite decision and is made at the endpoint — see 0013's header.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = new AdministrativeQuery(db);

            var atBranch = await query.MemberQueryAsync(
                new MemberQueryFilter(MemberNo: f.Prefix, BranchId: f.BranchId),
                FirstPage, ByMemberNo, PermittedPayers.Unrestricted, AsOf);

            atBranch.TotalCount.Should().Be(1);
            atBranch.Items[0].BranchId.Should().Be(f.BranchId);
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task An_identity_filter_that_matched_nobody_returns_nothing_not_everything()
    {
        // The failure mode this guards: patient-service matched no one, the empty list is read as "no filter",
        // and a failed name lookup answers with the entire membership.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = new AdministrativeQuery(db);

            var none = await query.MemberQueryAsync(
                new MemberQueryFilter(MemberNo: f.Prefix, BeneficiaryIds: []),
                FirstPage, ByMemberNo, PermittedPayers.Unrestricted, AsOf);
            none.TotalCount.Should().Be(0);

            var one = await query.MemberQueryAsync(
                new MemberQueryFilter(MemberNo: f.Prefix, BeneficiaryIds: [f.Members[0]]),
                FirstPage, ByMemberNo, PermittedPayers.Unrestricted, AsOf);
            one.TotalCount.Should().Be(1);
        }
        finally { await Cleanup(f); }
    }

    // ---- Sort + pagination -------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Sorting_by_utilization_orders_by_percentage_and_not_by_amount()
    {
        // The distinction matters for a charity: a member at 90% of a 100-unit ceiling needs attention before
        // one at 40% of 10 000, and sorting by consumed amount would put them the wrong way round.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = new AdministrativeQuery(db);

            var page = await query.MemberQueryAsync(
                new MemberQueryFilter(MemberNo: f.Prefix), FirstPage,
                new SortRequest("percentused", Descending: true), PermittedPayers.Unrestricted, AsOf);

            var percentages = page.Items.Select(m => m.PercentUsed ?? -1m).ToList();
            percentages.Should().BeInDescendingOrder();
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task Pagination_walks_the_whole_set_without_repeating_or_skipping_a_row()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = new AdministrativeQuery(db);

            var seen = new List<string>();
            for (var p = 1; p <= 5; p++)
            {
                var page = await query.MemberQueryAsync(
                    new MemberQueryFilter(MemberNo: f.Prefix), PageRequest.Of(p, 2), ByMemberNo,
                    PermittedPayers.Unrestricted, AsOf);
                seen.AddRange(page.Items.Select(i => i.MemberNo));
                if (page.Items.Count == 0) break;
            }

            seen.Should().OnlyHaveUniqueItems();
            seen.Should().HaveCount(f.Members.Count);
            seen.Should().BeInAscendingOrder(StringComparer.Ordinal);
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task A_policys_member_count_and_utilization_come_from_the_same_accumulator_the_report_reads()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var query = new AdministrativeQuery(db);

            var page = await query.PolicyQueryAsync(
                new PolicyQueryFilter(PayerId: f.PayerA), FirstPage, ByPolicyNo, PermittedPayers.Unrestricted);

            var row = page.Items.Single(p => p.PolicyId == f.PolicyA);
            row.MemberCount.Should().Be(3);
            row.TotalConsumed.Should().Be(85m + 0m + 120m);
            row.TotalLimit.Should().Be(300m);
        }
        finally { await Cleanup(f); }
    }

    // ---- Version in force --------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_service_date_inside_v1s_window_resolves_v1_even_though_v2_is_active_now()
    {
        // THE acceptance criterion for coverage details. A claim submitted late is judged by the rules that
        // existed when the care was given (design 38 §7.1) — resolving "the current version" would re-price
        // February against rules written in July.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var resolver = new PlanVersionResolver(db);

            var february = await resolver.ResolveAsync(f.PlanId, new DateOnly(2026, 2, 15));
            february!.PlanVersionId.Should().Be(f.VersionOneId);
            february.VersionNo.Should().Be(1);
            february.Status.Should().Be(PlanVersionStatus.Superseded,
                "a superseded version still resolves for service dates inside its own window");

            var august = await resolver.ResolveAsync(f.PlanId, new DateOnly(2026, 8, 15));
            august!.PlanVersionId.Should().Be(f.VersionTwoId);
            august.Status.Should().Be(PlanVersionStatus.Active);
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task Coverage_details_show_the_members_own_ceiling_beside_the_one_the_plan_now_grants()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var resolver = new PlanVersionResolver(db);

            var inForce = await resolver.ResolveAsync(f.PlanId, new DateOnly(2026, 8, 15));
            var rule = inForce!.Rules.Single(r => r.BenefitCategoryId == f.CategoryId);

            var coverage = await db.Coverages.AsNoTracking().Include(c => c.Limits)
                .FirstAsync(c => c.BeneficiaryId == f.Members[0] && !c.IsDeleted);

            var detail = CoverageDetailAssembler.Category(
                "LAB", rule, coverage, new DateOnly(2026, 1, 1), new DateOnly(2026, 8, 15));

            detail.Limit.Should().Be(100m, "the member's generated coverage");
            detail.ConfiguredLimit.Should().Be(250m, "v2 raised the ceiling after this member was enrolled");
            detail.LimitDiffersFromPlan.Should().BeTrue();
            detail.Consumed.Should().Be(85m);
        }
        finally { await Cleanup(f); }
    }

    // ---- Seed / teardown ---------------------------------------------------------------------------------

    private sealed record Fixture(
        string Prefix, Guid PayerA, Guid PayerB, Guid PolicyA, Guid PolicyB, Guid PlanId,
        Guid VersionOneId, Guid VersionTwoId, Guid PolicyPlanId, Guid CategoryId, Guid BranchId,
        IReadOnlyList<Guid> Members);

    /// <summary>
    /// Two payers, one policy each. Policy A carries three members:
    ///   · member 0 — 85 of 100 consumed (High), enrolled at a known branch, serving a waiting period to 31 Aug
    ///   · member 1 — 0 of 100 (Zero), no branch recorded (the pre-0013 shape)
    ///   · member 2 — 120 of 100 (Exhausted — a mid-period limit reduction, which is legitimate)
    /// The plan has TWO versions: v1 Jan–Jul with a 100 ceiling, v2 from Jul with 250. The members were
    /// generated under v1, so coverage details must show both numbers.
    /// </summary>
    private static async Task<Fixture> Seed()
    {
        await using var db = Ctx();
        var prefix = $"Q{Guid.NewGuid():N}"[..9].ToUpperInvariant();

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

        var payerA = NewPayer(prefix + "A");
        var payerB = NewPayer(prefix + "B");
        var plan = new Plan
        {
            PlanId = Guid.NewGuid(), TenantId = Tenant, PlanCode = $"{prefix}PL",
            NameEn = "Query", NameAr = "Query", Category = "Primary",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Payers.AddRange(payerA, payerB);
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var v1 = new PlanVersion
        {
            PlanVersionId = Guid.NewGuid(), TenantId = Tenant, PlanId = plan.PlanId, VersionNo = 1,
            EffectiveFrom = new DateOnly(2026, 1, 1), EffectiveTo = new DateOnly(2026, 7, 1),
            // Seeded as Draft and promoted below: benefit rules cannot be INSERTED under a non-Draft version
            // (0005's immutability trigger), which is the invariant 19.1 exists to hold.
            Status = PlanVersionStatus.Draft, ActivatedAt = null,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            Rules =
            [
                new BenefitRule
                {
                    RuleId = Guid.NewGuid(), TenantId = Tenant, BenefitCategoryId = category.BenefitCategoryId,
                    IsCovered = true, LimitType = LimitType.Annual, LimitValue = 100m,
                    ResetPeriod = ResetPeriod.Yearly, WaitingPeriodDays = 0, Exclusions = "[]",
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                },
            ],
        };
        var v2 = new PlanVersion
        {
            PlanVersionId = Guid.NewGuid(), TenantId = Tenant, PlanId = plan.PlanId, VersionNo = 2,
            EffectiveFrom = new DateOnly(2026, 7, 1), Status = PlanVersionStatus.Draft,
            ActivatedAt = null,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            Rules =
            [
                new BenefitRule
                {
                    RuleId = Guid.NewGuid(), TenantId = Tenant, BenefitCategoryId = category.BenefitCategoryId,
                    IsCovered = true, LimitType = LimitType.Annual, LimitValue = 250m,
                    ResetPeriod = ResetPeriod.Yearly, WaitingPeriodDays = 0, Exclusions = "[]",
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                },
            ],
        };

        var policyA = NewPolicy(prefix, payerA.PayerId, "A");
        var policyB = NewPolicy(prefix, payerB.PayerId, "B");
        db.PlanVersions.AddRange(v1, v2);
        db.Policies.AddRange(policyA, policyB);
        await db.SaveChangesAsync();

        // Promote to the states the resolver reads: v1 Superseded (still resolvable for dates inside its own
        // window), v2 Active. Done in SQL with the immutability trigger off, because the legitimate route —
        // authoring, validating and activating through the 19.1 endpoints — is not what this test is about.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.plan_version DISABLE TRIGGER trg_plan_version_immutable");
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE policy.plan_version SET status = 'Superseded', activated_at = now(), superseded_by_version_id = {1} WHERE plan_version_id = {0}",
                v1.PlanVersionId, v2.PlanVersionId);
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE policy.plan_version SET status = 'Active', activated_at = now() WHERE plan_version_id = {0}", v2.PlanVersionId);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.plan_version ENABLE TRIGGER trg_plan_version_immutable");
        }

        var policyPlanA = NewPolicyPlan(policyA.PolicyId, v1.PlanVersionId);
        var policyPlanB = NewPolicyPlan(policyB.PolicyId, v1.PlanVersionId);
        db.PolicyPlans.AddRange(policyPlanA, policyPlanB);
        await db.SaveChangesAsync();

        var branch = Guid.NewGuid();
        var members = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        decimal[] consumed = [85m, 0m, 120m, 10m];

        for (var i = 0; i < members.Length; i++)
        {
            var onA = i < 3;
            db.Enrollments.Add(new Enrollment
            {
                EnrollmentId = Guid.NewGuid(), TenantId = Tenant, BeneficiaryId = members[i],
                PolicyId = onA ? policyA.PolicyId : policyB.PolicyId,
                PolicyPlanId = onA ? policyPlanA.PolicyPlanId : policyPlanB.PolicyPlanId,
                MemberNo = $"{prefix}-{i:D3}",
                Relationship = Relationship.Principal,
                EffectiveFrom = new DateOnly(2026, 1, 1),
                WaitingPeriodEndsOn = i == 0 ? new DateOnly(2026, 8, 31) : null,
                BranchId = i == 0 ? branch : null,
                Status = EnrollmentStatus.Active,
                SourcePlanVersionId = v1.PlanVersionId,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });

            db.Coverages.Add(new Coverage
            {
                CoverageId = Guid.NewGuid(), TenantId = Tenant,
                PolicyId = onA ? policyA.PolicyId : policyB.PolicyId,
                BeneficiaryId = members[i], BenefitCategoryId = category.BenefitCategoryId,
                EffectiveFrom = new DateOnly(2026, 1, 1), Status = CoverageStatus.Active,
                SourcePlanVersionId = v1.PlanVersionId,
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

        return new Fixture(prefix, payerA.PayerId, payerB.PayerId, policyA.PolicyId, policyB.PolicyId,
            plan.PlanId, v1.PlanVersionId, v2.PlanVersionId, policyPlanA.PolicyPlanId,
            category.BenefitCategoryId, branch, members);
    }

    private static Payer NewPayer(string code) => new()
    {
        PayerId = Guid.NewGuid(), TenantId = Tenant, PayerCode = code[..Math.Min(10, code.Length)],
        NameEn = "Query Payer", NameAr = "Query Payer", PayerType = PayerType.Donor,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static Domain.Policy NewPolicy(string prefix, Guid payerId, string suffix) => new()
    {
        PolicyId = Guid.NewGuid(), TenantId = Tenant, PolicyNo = $"{prefix}-POL-{suffix}",
        PayerId = payerId, EffectiveFrom = new DateOnly(2026, 1, 1), Status = PolicyStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static PolicyPlan NewPolicyPlan(Guid policyId, Guid versionId) => new()
    {
        PolicyPlanId = Guid.NewGuid(), TenantId = Tenant, PolicyId = policyId, PlanVersionId = versionId,
        PlanLabel = "Standard", EffectiveFrom = new DateOnly(2026, 1, 1), IsDefault = true,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task Cleanup(Fixture f)
    {
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.plan_version DISABLE TRIGGER trg_plan_version_immutable");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.benefit_rule DISABLE TRIGGER trg_benefit_rule_immutable");
        try
        {
            Guid[] policies = [f.PolicyA, f.PolicyB];
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.coverage_limit WHERE coverage_id IN " +
                "(SELECT coverage_id FROM policy.coverage WHERE policy_id = ANY({0}))", [policies]);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.coverage WHERE policy_id = ANY({0})", [policies]);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.enrollment WHERE policy_id = ANY({0})", [policies]);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.policy_plan WHERE policy_id = ANY({0})", [policies]);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.policy WHERE policy_id = ANY({0})", [policies]);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.benefit_rule WHERE plan_version_id IN (SELECT plan_version_id FROM policy.plan_version WHERE plan_id = {0})", f.PlanId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.plan_version WHERE plan_id = {0}", f.PlanId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.plan WHERE plan_id = {0}", f.PlanId);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.payer WHERE payer_id = ANY({0})", [new[] { f.PayerA, f.PayerB }]);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.benefit_rule ENABLE TRIGGER trg_benefit_rule_immutable");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.plan_version ENABLE TRIGGER trg_plan_version_immutable");
        }
    }
}
