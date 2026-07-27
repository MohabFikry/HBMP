using FluentAssertions;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.1 against real Postgres — the three invariants that only the database can actually guarantee.
///
/// The API returns 409 on a write to an activated version, but an API check is a courtesy, not an invariant:
/// a repair script, a future endpoint, or a psql session walks straight past it. What makes "an Active plan
/// version is immutable" TRUE is the trigger in migration 0005, and what makes "the version in force on a
/// service date is unambiguous" true is the GiST exclusion constraint. Both are asserted here by attempting
/// the forbidden write directly through EF, with no endpoint in the way.
///
/// Env-gated on <c>POLICY_TEST_DB</c>; serialized via the policy-db collection; self-cleaning by plan.
/// </summary>
[Collection("policy-db")]
public class PlanVersionStoreTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("POLICY_TEST_DB");
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private static PolicyDbContext Ctx() => new(new DbContextOptionsBuilder<PolicyDbContext>()
        .UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static async Task<Guid> SeedPlan(PolicyDbContext db)
    {
        var plan = new Plan
        {
            PlanId = Guid.NewGuid(), TenantId = Tenant,
            PlanCode = "T" + Guid.NewGuid().ToString("N")[..12],
            NameEn = "Test plan", NameAr = "خطة اختبار", Category = "Primary",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        return plan.PlanId;
    }

    private static async Task<Guid> CategoryId(PolicyDbContext db, string code = "LAB") =>
        (await db.BenefitCategories.AsNoTracking().FirstAsync(c => c.Code == code)).BenefitCategoryId;

    private static PlanVersion Version(Guid planId, int no, DateOnly from, DateOnly? to, PlanVersionStatus status) => new()
    {
        PlanVersionId = Guid.NewGuid(), TenantId = Tenant, PlanId = planId, VersionNo = no,
        EffectiveFrom = from, EffectiveTo = to, Status = status,
        ActivatedAt = status == PlanVersionStatus.Draft ? null : DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task Cleanup(Guid planId)
    {
        await using var db = Ctx();
        // Raw SQL, in dependency order, for two reasons: the immutability triggers protect exactly the rows a
        // teardown has to remove (so the guards come off for the duration), and there is no EF navigation
        // between plan and plan_version — the FK is real in the database but invisible to the change tracker,
        // so EF cannot order the deletes itself.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.benefit_rule DISABLE TRIGGER trg_benefit_rule_immutable");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.benefit_rule_tier DISABLE TRIGGER trg_benefit_rule_tier_immutable");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.plan_version DISABLE TRIGGER trg_plan_version_immutable");
        try
        {
            // The supersede chain is a self-FK; it has to be broken before any version row can go.
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE policy.plan_version SET superseded_by_version_id = NULL WHERE plan_id = {0}", planId);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.benefit_rule_tier WHERE benefit_rule_id IN (SELECT rule_id FROM policy.benefit_rule " +
                "WHERE plan_version_id IN (SELECT plan_version_id FROM policy.plan_version WHERE plan_id = {0}))", planId);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.benefit_rule WHERE plan_version_id IN (SELECT plan_version_id FROM policy.plan_version WHERE plan_id = {0})", planId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.plan_version WHERE plan_id = {0}", planId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.plan WHERE plan_id = {0}", planId);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.benefit_rule ENABLE TRIGGER trg_benefit_rule_immutable");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.benefit_rule_tier ENABLE TRIGGER trg_benefit_rule_tier_immutable");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.plan_version ENABLE TRIGGER trg_plan_version_immutable");
        }
    }

    // ---- Immutability ------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task An_active_versions_effective_from_cannot_be_moved()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        Guid planId;
        await using (var db = Ctx())
        {
            planId = await SeedPlan(db);
            db.PlanVersions.Add(Version(planId, 1, new(2026, 1, 1), null, PlanVersionStatus.Active));
            await db.SaveChangesAsync();
        }
        try
        {
            await using var db = Ctx();
            var v = await db.PlanVersions.FirstAsync(x => x.PlanId == planId);
            v.EffectiveFrom = new DateOnly(2026, 3, 1);   // would silently re-date every past adjudication

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.MessageText.Should().Contain("immutable");
        }
        finally { await Cleanup(planId); }
    }

    [SkippableFact]
    public async Task The_benefit_rules_of_an_active_version_cannot_be_rewritten()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        Guid planId;
        Guid versionId;
        await using (var db = Ctx())
        {
            planId = await SeedPlan(db);
            var v = Version(planId, 1, new(2026, 1, 1), null, PlanVersionStatus.Draft);
            v.Rules.Add(new BenefitRule
            {
                RuleId = Guid.NewGuid(), TenantId = Tenant, PlanVersionId = v.PlanVersionId,
                BenefitCategoryId = await CategoryId(db), IsCovered = true,
                LimitType = LimitType.Annual, LimitValue = 5000m, ResetPeriod = ResetPeriod.Yearly,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.PlanVersions.Add(v);
            await db.SaveChangesAsync();
            versionId = v.PlanVersionId;

            // Draft → Active via the same transition the endpoint makes.
            v.Status = PlanVersionStatus.Active;
            v.ActivatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        try
        {
            await using var db = Ctx();
            var rule = await db.BenefitRules.FirstAsync(r => r.PlanVersionId == versionId);
            rule.LimitValue = 50_000m;   // a tenfold entitlement increase, applied retroactively to every member

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.MessageText.Should().Contain("immutable");
        }
        finally { await Cleanup(planId); }
    }

    [SkippableFact]
    public async Task The_cost_share_of_an_active_version_cannot_be_rewritten()
    {
        // 19.1b. Freezing plan_version and benefit_rule while leaving the per-tier AMOUNTS writable would
        // freeze the shape of the plan and none of its prices — which is most of what a plan is. Attempted
        // directly through EF, with no endpoint in the way.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        Guid planId;
        Guid ruleId;
        await using (var db = Ctx())
        {
            planId = await SeedPlan(db);
            var v = Version(planId, 1, new(2026, 1, 1), null, PlanVersionStatus.Draft);
            var rule = new BenefitRule
            {
                RuleId = Guid.NewGuid(), TenantId = Tenant, PlanVersionId = v.PlanVersionId,
                BenefitCategoryId = await CategoryId(db), IsCovered = true,
                LimitType = LimitType.Annual, LimitValue = 5000m, ResetPeriod = ResetPeriod.Yearly,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            rule.Tiers.Add(new BenefitRuleTier
            {
                RuleTierId = Guid.NewGuid(), TenantId = Tenant, BenefitRuleId = rule.RuleId,
                NetworkTierId = Guid.NewGuid(), TierCode = "T1", IsCovered = true, CopayPercent = 10m,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            v.Rules.Add(rule);
            db.PlanVersions.Add(v);
            await db.SaveChangesAsync();
            ruleId = rule.RuleId;

            v.Status = PlanVersionStatus.Active;
            v.ActivatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        try
        {
            await using var db = Ctx();
            var tier = await db.BenefitRuleTiers.FirstAsync(t => t.BenefitRuleId == ruleId);
            tier.CopayPercent = 40m;   // quadrupling the member's share of every past claim at this tier

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.MessageText.Should().Contain("immutable");
        }
        finally { await Cleanup(planId); }
    }

    [SkippableFact]
    public async Task Cost_share_may_not_be_added_to_an_active_version()
    {
        // The other half of the same rule: an activated version must not gain a tier it did not have. Without
        // the INSERT arm of the trigger, a plan that was validated as complete could grow a new price after
        // the fact — outside anyone's review.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        Guid planId;
        Guid ruleId;
        await using (var db = Ctx())
        {
            planId = await SeedPlan(db);
            var v = Version(planId, 1, new(2026, 1, 1), null, PlanVersionStatus.Draft);
            var rule = new BenefitRule
            {
                RuleId = Guid.NewGuid(), TenantId = Tenant, PlanVersionId = v.PlanVersionId,
                BenefitCategoryId = await CategoryId(db), IsCovered = true,
                LimitType = LimitType.Annual, LimitValue = 5000m, ResetPeriod = ResetPeriod.Yearly,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            v.Rules.Add(rule);
            db.PlanVersions.Add(v);
            await db.SaveChangesAsync();
            ruleId = rule.RuleId;

            v.Status = PlanVersionStatus.Active;
            v.ActivatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        try
        {
            await using var db = Ctx();
            db.BenefitRuleTiers.Add(new BenefitRuleTier
            {
                RuleTierId = Guid.NewGuid(), TenantId = Tenant, BenefitRuleId = ruleId,
                NetworkTierId = Guid.NewGuid(), TierCode = "OON", IsCovered = false,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.MessageText.Should().Contain("immutable");
        }
        finally { await Cleanup(planId); }
    }

    [SkippableFact]
    public async Task A_not_covered_tier_may_not_carry_cost_share()
    {
        // The database's own version of UNCOVERED_WITH_TIER_COST_SHARE: there is no amount to take a share OF,
        // and a stored co-pay under a not-covered row renders as an entitlement in every UI that reads it.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        Guid planId;
        await using var seed = Ctx();
        planId = await SeedPlan(seed);
        try
        {
            var v = Version(planId, 1, new(2026, 1, 1), null, PlanVersionStatus.Draft);
            var rule = new BenefitRule
            {
                RuleId = Guid.NewGuid(), TenantId = Tenant, PlanVersionId = v.PlanVersionId,
                BenefitCategoryId = await CategoryId(seed), IsCovered = true,
                LimitType = LimitType.Annual, LimitValue = 5000m, ResetPeriod = ResetPeriod.Yearly,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            rule.Tiers.Add(new BenefitRuleTier
            {
                RuleTierId = Guid.NewGuid(), TenantId = Tenant, BenefitRuleId = rule.RuleId,
                NetworkTierId = Guid.NewGuid(), TierCode = "OON",
                IsCovered = false, CopayPercent = 40m,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            v.Rules.Add(rule);
            seed.PlanVersions.Add(v);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => seed.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.ConstraintName.Should().Be("ck_brt_uncovered_has_no_cost_share");
        }
        finally { await Cleanup(planId); }
    }

    [SkippableFact]
    public async Task A_draft_is_freely_editable()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        Guid planId;
        await using (var db = Ctx())
        {
            planId = await SeedPlan(db);
            var v = Version(planId, 1, new(2026, 1, 1), null, PlanVersionStatus.Draft);
            v.Rules.Add(new BenefitRule
            {
                RuleId = Guid.NewGuid(), TenantId = Tenant, PlanVersionId = v.PlanVersionId,
                BenefitCategoryId = await CategoryId(db), IsCovered = true,
                LimitType = LimitType.Annual, LimitValue = 1000m, ResetPeriod = ResetPeriod.Yearly,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.PlanVersions.Add(v);
            await db.SaveChangesAsync();
        }
        try
        {
            await using var db = Ctx();
            var rule = await db.BenefitRules.FirstAsync(r => db.PlanVersions.Any(v => v.PlanId == planId && v.PlanVersionId == r.PlanVersionId));
            rule.LimitValue = 2000m;
            var version = await db.PlanVersions.FirstAsync(v => v.PlanId == planId);
            version.EffectiveFrom = new DateOnly(2026, 2, 1);
            await db.SaveChangesAsync();   // the point of a draft: it is still being authored

            (await db.BenefitRules.AsNoTracking().FirstAsync(r => r.RuleId == rule.RuleId)).LimitValue.Should().Be(2000m);
        }
        finally { await Cleanup(planId); }
    }

    [SkippableFact]
    public async Task A_superseded_version_cannot_be_reactivated()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        Guid planId;
        await using (var db = Ctx())
        {
            planId = await SeedPlan(db);
            db.PlanVersions.Add(Version(planId, 1, new(2026, 1, 1), new(2026, 7, 1), PlanVersionStatus.Superseded));
            await db.SaveChangesAsync();
        }
        try
        {
            await using var db = Ctx();
            var v = await db.PlanVersions.FirstAsync(x => x.PlanId == planId);
            v.Status = PlanVersionStatus.Active;

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.MessageText.Should().Contain("cannot be reactivated");
        }
        finally { await Cleanup(planId); }
    }

    // ---- Overlap exclusion -------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Two_resolvable_versions_of_a_plan_cannot_cover_the_same_day()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        Guid planId;
        await using (var db = Ctx())
        {
            planId = await SeedPlan(db);
            db.PlanVersions.Add(Version(planId, 1, new(2026, 1, 1), new(2026, 7, 1), PlanVersionStatus.Superseded));
            await db.SaveChangesAsync();
        }
        try
        {
            await using var db = Ctx();
            // Starts one day before the incumbent ends — a single overlapping day is enough to make the
            // resolver ambiguous, which is the whole failure this constraint prevents.
            db.PlanVersions.Add(Version(planId, 2, new(2026, 6, 30), null, PlanVersionStatus.Active));

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be("23P01");   // exclusion_violation
        }
        finally { await Cleanup(planId); }
    }

    [SkippableFact]
    public async Task Versions_that_abut_exactly_are_allowed()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        Guid planId;
        await using (var db = Ctx())
        {
            planId = await SeedPlan(db);
            db.PlanVersions.Add(Version(planId, 1, new(2026, 1, 1), new(2026, 7, 1), PlanVersionStatus.Superseded));
            await db.SaveChangesAsync();
        }
        try
        {
            await using var db = Ctx();
            // The successor starts on exactly the predecessor's exclusive end date: no gap, no double cover.
            // If the range were inclusive ('[]') this would be rejected and every handover would need a
            // one-day hole for a service date to fall through.
            db.PlanVersions.Add(Version(planId, 2, new(2026, 7, 1), null, PlanVersionStatus.Active));
            await db.SaveChangesAsync();

            (await db.PlanVersions.CountAsync(v => v.PlanId == planId)).Should().Be(2);
        }
        finally { await Cleanup(planId); }
    }

    [SkippableFact]
    public async Task Drafts_are_exempt_from_the_overlap_rule()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        Guid planId;
        await using (var db = Ctx())
        {
            planId = await SeedPlan(db);
            db.PlanVersions.Add(Version(planId, 1, new(2026, 1, 1), null, PlanVersionStatus.Active));
            await db.SaveChangesAsync();
        }
        try
        {
            await using var db = Ctx();
            // An amendment is authored while its predecessor is still live and still open-ended, so a draft
            // MUST be allowed to overlap. It only has to be disjoint at the moment it activates.
            db.PlanVersions.Add(Version(planId, 2, new(2026, 3, 1), null, PlanVersionStatus.Draft));
            await db.SaveChangesAsync();

            (await db.PlanVersions.CountAsync(v => v.PlanId == planId)).Should().Be(2);
        }
        finally { await Cleanup(planId); }
    }

    // ---- The resolver ------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task The_resolver_returns_the_version_in_force_on_the_service_date()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        Guid planId;
        Guid v1Id;
        Guid v2Id;
        await using (var db = Ctx())
        {
            planId = await SeedPlan(db);
            var v1 = Version(planId, 1, new(2026, 1, 1), new(2026, 7, 1), PlanVersionStatus.Superseded);
            var v2 = Version(planId, 2, new(2026, 7, 1), null, PlanVersionStatus.Active);
            db.PlanVersions.AddRange(v1, v2);
            await db.SaveChangesAsync();
            v1Id = v1.PlanVersionId;
            v2Id = v2.PlanVersionId;
        }
        try
        {
            await using var db = Ctx();
            var resolver = new PlanVersionResolver(db);

            // A date inside the superseded window resolves to the SUPERSEDED version — this is the property
            // the whole module exists for. Resolving "the active version" here would adjudicate June's care
            // against July's rules.
            (await resolver.ResolveAsync(planId, new DateOnly(2026, 6, 15)))!.PlanVersionId.Should().Be(v1Id);
            (await resolver.ResolveAsync(planId, new DateOnly(2026, 7, 15)))!.PlanVersionId.Should().Be(v2Id);

            // The boundary days, where an inclusive/exclusive slip would go unnoticed for months.
            (await resolver.ResolveAsync(planId, new DateOnly(2026, 1, 1)))!.PlanVersionId.Should().Be(v1Id);
            (await resolver.ResolveAsync(planId, new DateOnly(2026, 6, 30)))!.PlanVersionId.Should().Be(v1Id);
            (await resolver.ResolveAsync(planId, new DateOnly(2026, 7, 1)))!.PlanVersionId.Should().Be(v2Id);

            // Before any version existed there is no answer — and "no configuration" must not silently
            // become "the earliest one we have".
            (await resolver.ResolveAsync(planId, new DateOnly(2025, 12, 31))).Should().BeNull();
        }
        finally { await Cleanup(planId); }
    }

    [SkippableFact]
    public async Task The_resolver_never_returns_a_draft()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        Guid planId;
        await using (var db = Ctx())
        {
            planId = await SeedPlan(db);
            db.PlanVersions.Add(Version(planId, 1, new(2026, 1, 1), null, PlanVersionStatus.Draft));
            await db.SaveChangesAsync();
        }
        try
        {
            await using var db = Ctx();
            // A draft has never been in force. Resolving one would adjudicate against rules nobody approved.
            (await new PlanVersionResolver(db).ResolveAsync(planId, new DateOnly(2026, 3, 1))).Should().BeNull();
        }
        finally { await Cleanup(planId); }
    }

    [SkippableFact]
    public async Task The_resolver_loads_the_versions_benefit_rules()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        Guid planId;
        await using (var db = Ctx())
        {
            planId = await SeedPlan(db);
            var v = Version(planId, 1, new(2026, 1, 1), null, PlanVersionStatus.Draft);
            v.Rules.Add(new BenefitRule
            {
                RuleId = Guid.NewGuid(), TenantId = Tenant, PlanVersionId = v.PlanVersionId,
                BenefitCategoryId = await CategoryId(db), IsCovered = true,
                LimitType = LimitType.Annual, LimitValue = 5000m, ResetPeriod = ResetPeriod.Yearly,
                WaitingPeriodDays = 30,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.PlanVersions.Add(v);
            await db.SaveChangesAsync();
            v.Status = PlanVersionStatus.Active;
            v.ActivatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        try
        {
            await using var db = Ctx();
            // 19.2 generates a member's coverage + limits from exactly these rows, so a resolver that
            // returned the version without its rules would silently generate an empty entitlement.
            var resolved = await new PlanVersionResolver(db).ResolveAsync(planId, new DateOnly(2026, 3, 1));
            resolved!.Rules.Should().ContainSingle()
                .Which.Should().Match<BenefitRule>(r => r.LimitValue == 5000m && r.WaitingPeriodDays == 30);
        }
        finally { await Cleanup(planId); }
    }
}
