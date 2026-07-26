using FluentAssertions;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Tests;

/// <summary>Batching at the datastore (env-gated <c>CLAIMS_TEST_DB</c>). Proves: (1) the full lifecycle
/// Open→UnderReview→Decided→SettlementIssued→Closed with rollups recomputed then FROZEN; (2) Decided is blocked while
/// any line is undecided (422); (3) illegal transitions are rejected; (4) parallel attempts to batch the SAME claim
/// let exactly one win — the single-open-batch DB index proof. Self-cleans by tenant scope.</summary>
[Collection("claims-db")]
public class BatchIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("CLAIMS_TEST_DB");
    private static DbContextOptions<ClaimsDbContext> Options() =>
        new DbContextOptionsBuilder<ClaimsDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;
    private static ClaimsDbContext Ctx() => new(Options());
    private static BatchService Svc(ClaimsDbContext db) => new(db, new BatchNoIssuer(db), new BatchRollupService(db), TimeProvider.System);

    private static async Task<Guid> SeedClaim(string tenant, Guid provider, ClaimLineStatus lineStatus, decimal allowed)
    {
        await using var db = Ctx();
        var claim = new Claim
        {
            ClaimId = Guid.NewGuid(), ClaimNo = await new ClaimNoIssuer(db).NextAsync(2026),
            Origin = ClaimOrigin.AutoDerived, BeneficiaryId = Guid.NewGuid(), ProviderId = provider,
            TenantId = tenant, ServiceDateFrom = new DateOnly(2026, 7, 1), ServiceDateTo = new DateOnly(2026, 7, 1),
            CurrencyCode = "EGP", ClaimedAmount = 200, PricedAmount = 180, Status = ClaimStatus.UnderAdjudication,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        claim.Lines.Add(new ClaimLine
        {
            ClaimLineId = Guid.NewGuid(), ClaimId = claim.ClaimId, CodeSystem = ClaimCodeSystem.CPT, Code = "80053",
            Quantity = 1, BilledAmount = 200, ContractPrice = 180, AllowedAmount = allowed, Status = lineStatus,
            FulfillmentType = FulfillmentType.None,
        });
        db.Claims.Add(claim);
        await db.SaveChangesAsync();
        return claim.ClaimId;
    }

    private static BatchSelector Manual(string _, DateOnly period, Guid provider, params Guid[] claimIds) =>
        new(BatchType.Provider, BatchSelectionMode.Manual, provider, null, null, period, period, null, null, claimIds);

    [SkippableFact]
    public async Task Full_lifecycle_recomputes_then_freezes_rollups()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var provider = Guid.NewGuid();
        try
        {
            var claimId = await SeedClaim(tenant, provider, ClaimLineStatus.Approved, allowed: 180);
            var period = new DateOnly(2026, 7, 1);

            await using var db = Ctx();
            var svc = Svc(db);
            var created = await svc.CreateAsync(tenant, "officer-1", Manual(tenant, period, provider, claimId), default);
            created.Outcome.Should().Be(BatchOutcome.Ok);
            created.Batch!.TotalApproved.Should().Be(180);
            var id = created.Batch.BatchId;

            (await svc.TransitionAsync(tenant, id, BatchStatus.UnderReview, null, default)).Outcome.Should().Be(BatchOutcome.Ok);
            (await svc.TransitionAsync(tenant, id, BatchStatus.Decided, null, default)).Outcome.Should().Be(BatchOutcome.Ok);
            var issued = await svc.TransitionAsync(tenant, id, BatchStatus.SettlementIssued, null, default);
            issued.Outcome.Should().Be(BatchOutcome.Ok);
            issued.Batch!.FrozenAt.Should().NotBeNull();
            (await svc.TransitionAsync(tenant, id, BatchStatus.Closed, null, default)).Outcome.Should().Be(BatchOutcome.Ok);

            await using var verify = Ctx();
            var b = await verify.ClaimBatches.AsNoTracking().SingleAsync(x => x.BatchId == id);
            b.Status.Should().Be(BatchStatus.Closed);
            b.NetPayable.Should().Be(180, "rollups were frozen at SettlementIssued");
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Decided_is_blocked_while_a_line_is_undecided()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var provider = Guid.NewGuid();
        try
        {
            var claimId = await SeedClaim(tenant, provider, ClaimLineStatus.Pending, allowed: 0);
            var period = new DateOnly(2026, 7, 1);
            await using var db = Ctx();
            var svc = Svc(db);
            var id = (await svc.CreateAsync(tenant, "o", Manual(tenant, period, provider, claimId), default)).Batch!.BatchId;
            await svc.TransitionAsync(tenant, id, BatchStatus.UnderReview, null, default);

            var decide = await svc.TransitionAsync(tenant, id, BatchStatus.Decided, null, default);
            decide.Outcome.Should().Be(BatchOutcome.UndecidedLines);
            decide.UndecidedClaimLines.Should().HaveCount(1);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Illegal_transition_is_rejected()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var provider = Guid.NewGuid();
        try
        {
            var claimId = await SeedClaim(tenant, provider, ClaimLineStatus.Approved, allowed: 180);
            var period = new DateOnly(2026, 7, 1);
            await using var db = Ctx();
            var svc = Svc(db);
            var id = (await svc.CreateAsync(tenant, "o", Manual(tenant, period, provider, claimId), default)).Batch!.BatchId;
            // Open → Decided is illegal (must go through review).
            (await svc.TransitionAsync(tenant, id, BatchStatus.Decided, null, default)).Outcome.Should().Be(BatchOutcome.IllegalTransition);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Parallel_add_of_the_same_claim_lets_exactly_one_win()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var provider = Guid.NewGuid();
        try
        {
            var claimId = await SeedClaim(tenant, provider, ClaimLineStatus.Approved, allowed: 180);
            var period = new DateOnly(2026, 7, 1);
            // Two empty open batches for the same payee; race adding the same claim into both.
            Guid b1, b2;
            await using (var db = Ctx())
            {
                var svc = Svc(db);
                b1 = (await svc.CreateAsync(tenant, "o", Empty(period, provider), default)).Batch!.BatchId;
                b2 = (await svc.CreateAsync(tenant, "o", Empty(period, provider), default)).Batch!.BatchId;
            }

            var targets = new[] { b1, b2, b1, b2, b1, b2 };
            var outcomes = await Task.WhenAll(targets.Select(async bid =>
            {
                await using var db = Ctx();
                return (await Svc(db).AddClaimAsync(tenant, "o", bid, claimId, default)).Outcome;
            }));

            outcomes.Count(o => o == BatchOutcome.Ok).Should().Be(1, "a claim may live in only one open batch");
            outcomes.Where(o => o != BatchOutcome.Ok).Should().OnlyContain(o => o == BatchOutcome.AlreadyBatched);

            await using var verify = Ctx();
            (await verify.ClaimBatchItems.CountAsync(i => i.ClaimId == claimId && i.RemovedAt == null))
                .Should().Be(1, "the single-open-batch DB index makes a second live membership impossible");
        }
        finally { await Cleanup(tenant); }
    }

    private static BatchSelector Empty(DateOnly period, Guid provider) =>
        new(BatchType.Provider, BatchSelectionMode.Manual, provider, null, null, period, period, null, null, []);

    private static async Task Cleanup(string tenant)
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM claims.claim_batch_item WHERE batch_id IN (SELECT batch_id FROM claims.claim_batch WHERE tenant_id = {0}); " +
            "DELETE FROM claims.claim_batch WHERE tenant_id = {0}; " +
            "DELETE FROM claims.claim_line WHERE claim_id IN (SELECT claim_id FROM claims.claim WHERE tenant_id = {0}); " +
            "DELETE FROM claims.claim WHERE tenant_id = {0};", tenant);
    }
}
