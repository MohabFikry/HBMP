using FluentAssertions;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Tests;

/// <summary>THE no-double-billing proof (Phase 10b.1) against REAL parallel PostgreSQL transactions (env-gated
/// <c>CLAIMS_TEST_DB</c>, not mocked): N racers each try to create a payable claim_line for the SAME fulfillment_ref
/// but with DISTINCT event ids — the partial unique index <c>ux_claim_line_fulfillment</c> lets EXACTLY ONE win; the
/// rest are DUPLICATE. Exactly one non-Void line survives in the DB. Self-cleans by tenant scope.</summary>
[Collection("claims-db")]
public class ClaimsConcurrencyTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("CLAIMS_TEST_DB");
    private static DbContextOptions<ClaimsDbContext> Options() =>
        new DbContextOptionsBuilder<ClaimsDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;
    private static ClaimsDbContext Ctx() => new(Options());

    [SkippableFact]
    public async Task Parallel_intake_of_one_fulfillment_ref_lets_exactly_one_win()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var fref = Guid.NewGuid();
        var beneficiary = Guid.NewGuid();
        var provider = Guid.NewGuid();
        try
        {
            const int racers = 8;
            var tasks = Enumerable.Range(0, racers).Select(async _ =>
            {
                await using var db = Ctx();
                var ev = new ClaimIntakeEvent(
                    Guid.NewGuid(), "OrderLinesConsumed", tenant, fref, FulfillmentType.OrderFulfillment,
                    beneficiary, provider, null, null, ClaimCodeSystem.CPT, "80053", "Metabolic panel",
                    1, 200m, new DateOnly(2026, 7, 1), "EGP", DateTimeOffset.UtcNow);
                var exec = new ClaimIntakeExecutor(db, new ClaimNoIssuer(db), new FixedTariff(150m), TimeProvider.System);
                return await exec.IngestAsync(ev, null);
            });
            var outcomes = await Task.WhenAll(tasks);

            outcomes.Count(o => o.Outcome == IntakeOutcome.Created).Should().Be(1, "exactly one racer may create the payable line");
            outcomes.Where(o => o.Outcome != IntakeOutcome.Created)
                .Should().OnlyContain(o => o.Outcome == IntakeOutcome.Duplicate,
                    "every loser is a DUPLICATE_CLAIM, never a silent second payable line");

            await using var verify = Ctx();
            (await verify.ClaimLines.CountAsync(l => l.FulfillmentRef == fref && l.Status != ClaimLineStatus.Void))
                .Should().Be(1, "the DB unique index makes a second live line impossible");
        }
        finally
        {
            await using var db = Ctx();
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM claims.claim_line WHERE claim_id IN (SELECT claim_id FROM claims.claim WHERE tenant_id = {0}); " +
                "DELETE FROM claims.claim WHERE tenant_id = {0};", tenant);
        }
    }
}
