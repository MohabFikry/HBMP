using System.Text.Json;
using FluentAssertions;
using Mersal.Eligibility.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Eligibility.Tests;

/// <summary>
/// Phase 18.A4 — a policy's status must cascade to its coverages in BOTH directions.
///
/// Only non-Active was ever written, so suspending a policy correctly suspended its coverages but
/// REACTIVATING it never restored them: the member stayed ineligible with no route back short of a manual
/// database edit. Env-gated on <c>ELIGIBILITY_TEST_DB</c>; self-cleans by policy number.
/// </summary>
public class PolicyCascadeTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly string? Db =
        Environment.GetEnvironmentVariable("ELIGIBILITY_TEST_DB")
        ?? Environment.GetEnvironmentVariable("ELIGIBILITY_TEST_DB_OWNER");

    private static EligibilityDbContext Ctx() => new(new DbContextOptionsBuilder<EligibilityDbContext>()
        .UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static ProjectionUpdater Updater(EligibilityDbContext db) =>
        new(db, new InMemoryEligibilityCache(), TimeProvider.System);

    private static string PolicyEvent(string policyNo, string status) =>
        JsonSerializer.Serialize(new { policyId = Guid.NewGuid(), policyNo, status }, Web);

    [SkippableFact]
    public async Task Reactivating_a_policy_restores_its_coverages()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var policyNo = "A4-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await Seed(policyNo, "Active");

            await using (var db = Ctx())
                await Updater(db).ApplyAsync(Guid.NewGuid(), "PolicyChanged", PolicyEvent(policyNo, "Suspended"));
            (await StatusOf(policyNo)).Should().Be("Suspended", "a suspended policy cascades to its coverages");

            await using (var db = Ctx())
                await Updater(db).ApplyAsync(Guid.NewGuid(), "PolicyChanged", PolicyEvent(policyNo, "Active"));

            (await StatusOf(policyNo)).Should().Be("Active",
                "reactivating the policy must restore the coverage — this was a one-way door before 18.A4");
        }
        finally { await Cleanup(policyNo); }
    }

    [SkippableFact]
    public async Task An_expired_policy_still_cascades()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var policyNo = "A4-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await Seed(policyNo, "Active");
            await using (var db = Ctx())
                await Updater(db).ApplyAsync(Guid.NewGuid(), "PolicyChanged", PolicyEvent(policyNo, "Expired"));

            (await StatusOf(policyNo)).Should().Be("Expired");
        }
        finally { await Cleanup(policyNo); }
    }

    private static async Task Seed(string policyNo, string status)
    {
        await using var db = Ctx();
        db.Coverages.Add(new CoverageProjection
        {
            TenantId = "11111111-1111-1111-1111-111111111111",
            CoverageId = Guid.NewGuid(), BeneficiaryId = Guid.NewGuid(),
            BenefitCategory = "CONSULT", PolicyNo = policyNo, Status = status,
            EffectiveFrom = new DateOnly(2026, 1, 1), UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<string> StatusOf(string policyNo)
    {
        await using var db = Ctx();
        return await db.Coverages.AsNoTracking().Where(c => c.PolicyNo == policyNo)
            .Select(c => c.Status).SingleAsync();
    }

    private static async Task Cleanup(string policyNo)
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = Ctx();
        await db.Coverages.Where(c => c.PolicyNo == policyNo).ExecuteDeleteAsync();
    }
}
