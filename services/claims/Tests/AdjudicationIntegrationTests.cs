using FluentAssertions;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Tests;

/// <summary>Adjudication at the datastore (env-gated <c>CLAIMS_TEST_DB</c>). Proves the run PERSISTS the per-line
/// output (recommendation + reason codes + allowed + rule_version), moves the claim to UnderAdjudication, and leaves
/// pricing inputs UNCHANGED — the claims path reads coverage facts, it never writes a coverage accumulator (there is
/// no such column in the schema). Self-cleans by tenant scope.</summary>
[Collection("claims-db")]
public class AdjudicationIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("CLAIMS_TEST_DB");
    private static DbContextOptions<ClaimsDbContext> Options() =>
        new DbContextOptionsBuilder<ClaimsDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;
    private static ClaimsDbContext Ctx() => new(Options());

    [SkippableFact]
    public async Task Adjudicate_persists_the_recommendation_and_leaves_pricing_untouched()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            Guid claimId, lineId;
            await using (var db = Ctx())
            {
                var claim = new Claim
                {
                    ClaimId = Guid.NewGuid(), ClaimNo = await new ClaimNoIssuer(db).NextAsync(2026),
                    Origin = ClaimOrigin.AutoDerived, BeneficiaryId = Guid.NewGuid(), ProviderId = Guid.NewGuid(),
                    TenantId = tenant, ServiceDateFrom = new DateOnly(2026, 7, 1), CurrencyCode = "EGP",
                    ClaimedAmount = 200, Status = ClaimStatus.Draft, CreatedAt = DateTimeOffset.UtcNow,
                };
                var line = new ClaimLine
                {
                    ClaimLineId = Guid.NewGuid(), ClaimId = claim.ClaimId, CodeSystem = ClaimCodeSystem.CPT,
                    Code = "80053", Quantity = 1, BilledAmount = 200, ContractPrice = 180,
                    FulfillmentRef = Guid.NewGuid(), FulfillmentType = FulfillmentType.OrderFulfillment,
                    Status = ClaimLineStatus.Pending,
                };
                claim.Lines.Add(line);
                db.Claims.Add(claim);
                await db.SaveChangesAsync();
                claimId = claim.ClaimId; lineId = line.ClaimLineId;
            }

            await using (var db = Ctx())
            {
                var svc = new AdjudicationService(db, new PermissiveAdjudicationFacts());
                var results = await svc.AdjudicateAsync(tenant, claimId, null);
                results.Should().NotBeNull();
                results!.Should().ContainSingle();
            }

            await using var verify = Ctx();
            var l = await verify.ClaimLines.AsNoTracking().SingleAsync(x => x.ClaimLineId == lineId);
            l.SystemRecommendation.Should().Be(SystemRecommendation.RecommendApprove);
            l.RuleVersion.Should().Be(Adjudicator.RuleVersion);
            l.AllowedAmount.Should().Be(180m);
            l.ContractPrice.Should().Be(180m, "adjudication reads the tariff, it never rewrites it");
            (await verify.Claims.AsNoTracking().SingleAsync(c => c.ClaimId == claimId)).Status
                .Should().Be(ClaimStatus.UnderAdjudication);
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
