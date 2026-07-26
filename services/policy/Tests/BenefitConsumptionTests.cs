using FluentAssertions;
using Mersal.Events;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using PolicyEntity = Mersal.Policy.Domain.Policy;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 18.A1 / audit R2 X1 — the accumulator must actually bind.
///
/// Before this suite existed, <c>coverage_limit.consumed_value</c> was only ever written as 0, so
/// <c>remaining</c> always equalled the full limit, <c>LIMIT_EXCEEDED</c> could never fire, and every
/// member was eligible forever. These tests exercise the real writer
/// (<see cref="BenefitConsumptionApplier"/>) against real Postgres.
///
/// Env-gated on <c>POLICY_TEST_DB</c> (schema-owner conn); serialized via the policy-db collection;
/// self-cleaning by beneficiary scope.
/// </summary>
[Collection("policy-db")]
public class BenefitConsumptionTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("POLICY_TEST_DB");
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private static PolicyDbContext Ctx() => new(new DbContextOptionsBuilder<PolicyDbContext>()
        .UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static BenefitConsumptionApplier Applier(PolicyDbContext db) =>
        new(db, new InMemoryOutbox(), TimeProvider.System);

    private static ConsumptionInstruction Consume(Guid beneficiary, decimal qty, string key,
        string category = "LAB", ConsumptionDirection direction = ConsumptionDirection.Applied) =>
        new(Guid.NewGuid(), "OrderLinesConsumed", Tenant, beneficiary, category,
            BenefitAccumulation.SourceRef("OrderLinesConsumed", Guid.NewGuid(), key, direction),
            qty, direction, DateOnly.FromDateTime(DateTime.UtcNow));

    [SkippableFact]
    public async Task Consuming_a_line_increments_coverage_limit_consumed_value_exactly_once()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var limitId = await SeedCoverage(beneficiary, LimitType.Annual, limit: 10m);

            await using var db = Ctx();
            var result = await Applier(db).ApplyAsync(Consume(beneficiary, 2m, "key-1"));

            result.Outcome.Should().Be(ConsumptionOutcome.Applied);
            result.MovedLimits.Should().ContainSingle();
            (await ConsumedValue(limitId)).Should().Be(2m);
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task Replayed_consume_event_does_not_double_count()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var limitId = await SeedCoverage(beneficiary, LimitType.Annual, limit: 10m);
            var instruction = Consume(beneficiary, 2m, "key-replay");

            await using var db = Ctx();
            var applier = Applier(db);
            (await applier.ApplyAsync(instruction)).Outcome.Should().Be(ConsumptionOutcome.Applied);

            // Same source_ref redelivered (at-least-once) — must be a pure no-op.
            var replay = await applier.ApplyAsync(instruction);
            replay.Outcome.Should().Be(ConsumptionOutcome.Replayed);
            replay.MovedLimits.Should().BeEmpty();

            (await ConsumedValue(limitId)).Should().Be(2m);
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task Eligibility_flips_to_limit_exceeded_at_the_boundary()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var limitId = await SeedCoverage(beneficiary, LimitType.Count, limit: 3m);

            await using var db = Ctx();
            var applier = Applier(db);
            await applier.ApplyAsync(Consume(beneficiary, 2m, "boundary-1"));

            // Below the boundary: remaining > 0, so EligibilityEngine step (3) still says Eligible.
            (await Remaining(limitId)).Should().Be(1m);

            await applier.ApplyAsync(Consume(beneficiary, 1m, "boundary-2"));

            // At the boundary: remaining <= 0 is exactly the condition EligibilityEngine gates on
            // (services/eligibility/Domain/EligibilityEngine.cs step 3 → NeedsAuthorization + LIMIT reason).
            (await Remaining(limitId)).Should().Be(0m);
            (await ConsumedValue(limitId)).Should().Be(3m);
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task A_void_reverses_the_accumulator_symmetrically_and_cannot_go_negative()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var limitId = await SeedCoverage(beneficiary, LimitType.Annual, limit: 10m);

            await using var db = Ctx();
            var applier = Applier(db);
            await applier.ApplyAsync(Consume(beneficiary, 3m, "void-apply"));
            (await ConsumedValue(limitId)).Should().Be(3m);

            var reversal = await applier.ApplyAsync(
                Consume(beneficiary, 3m, "void-reverse", direction: ConsumptionDirection.Reversed));
            reversal.Outcome.Should().Be(ConsumptionOutcome.Reversed);
            (await ConsumedValue(limitId)).Should().Be(0m);

            // A reversal beyond what was consumed is REFUSED, never clamped to a false zero.
            var overshoot = await applier.ApplyAsync(
                Consume(beneficiary, 1m, "void-overshoot", direction: ConsumptionDirection.Reversed));
            overshoot.Outcome.Should().Be(ConsumptionOutcome.WouldGoNegative);
            (await ConsumedValue(limitId)).Should().Be(0m);
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task An_unmapped_service_records_a_visible_no_move_rather_than_a_silent_skip()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var limitId = await SeedCoverage(beneficiary, LimitType.Annual, limit: 10m);

            await using var db = Ctx();
            // A Procedure order carries no canonical benefit category (22 §11) — the accumulator must not
            // move, and the decision must be recorded in the ledger, not swallowed.
            var result = await Applier(db).ApplyAsync(Consume(beneficiary, 2m, "no-category", category: null!));

            result.Outcome.Should().Be(ConsumptionOutcome.NoBenefitCategory);
            (await ConsumedValue(limitId)).Should().Be(0m);

            await using var verify = Ctx();
            var ledger = await verify.BenefitConsumptions.AsNoTracking()
                .SingleAsync(r => r.BeneficiaryId == beneficiary);
            ledger.Outcome.Should().Be(ConsumptionOutcome.NoBenefitCategory);
            ledger.MovedLimits.Should().Be(0);
        }
        finally { await Cleanup(beneficiary); }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────

    private static async Task<Guid> SeedCoverage(Guid beneficiary, LimitType limitType, decimal limit)
    {
        await using var db = Ctx();
        var categoryId = await db.BenefitCategories.AsNoTracking()
            .Where(c => c.Code == "LAB").Select(c => c.BenefitCategoryId).FirstAsync();

        var policy = new PolicyEntity
        {
            PolicyId = Guid.NewGuid(), TenantId = Tenant, PolicyNo = "A1-" + Guid.NewGuid().ToString("N")[..12],
            EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var limitId = Guid.NewGuid();
        var coverage = new Coverage
        {
            CoverageId = Guid.NewGuid(), TenantId = Tenant, PolicyId = policy.PolicyId, BeneficiaryId = beneficiary,
            BenefitCategoryId = categoryId, EffectiveFrom = policy.EffectiveFrom, Status = CoverageStatus.Active,
            Limits =
            [
                new CoverageLimit
                {
                    CoverageLimitId = limitId, TenantId = Tenant, LimitType = limitType,
                    LimitValue = limit, ConsumedValue = 0m, ResetPeriod = ResetPeriod.None,
                },
            ],
        };
        // Two saves: there is no Policy→Coverage navigation, so EF cannot order the FK insert itself.
        db.Policies.Add(policy);
        await db.SaveChangesAsync();
        db.Coverages.Add(coverage);
        await db.SaveChangesAsync();
        return limitId;
    }

    private static async Task<decimal> ConsumedValue(Guid limitId)
    {
        await using var db = Ctx();
        return await db.CoverageLimits.AsNoTracking().Where(l => l.CoverageLimitId == limitId)
            .Select(l => l.ConsumedValue).SingleAsync();
    }

    private static async Task<decimal> Remaining(Guid limitId)
    {
        await using var db = Ctx();
        var l = await db.CoverageLimits.AsNoTracking().SingleAsync(x => x.CoverageLimitId == limitId);
        return l.Remaining;
    }

    private static async Task Cleanup(Guid beneficiary)
    {
        await using var db = Ctx();
        var coverageIds = await db.Coverages.Where(c => c.BeneficiaryId == beneficiary)
            .Select(c => c.CoverageId).ToListAsync();
        var policyIds = await db.Coverages.Where(c => c.BeneficiaryId == beneficiary)
            .Select(c => c.PolicyId).ToListAsync();
        await db.BenefitConsumptions.Where(r => r.BeneficiaryId == beneficiary).ExecuteDeleteAsync();
        await db.CoverageLimits.Where(l => coverageIds.Contains(l.CoverageId)).ExecuteDeleteAsync();
        await db.Coverages.Where(c => coverageIds.Contains(c.CoverageId)).ExecuteDeleteAsync();
        await db.Policies.Where(p => policyIds.Contains(p.PolicyId)).ExecuteDeleteAsync();
    }
}
