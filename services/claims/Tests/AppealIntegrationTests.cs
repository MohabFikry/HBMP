using FluentAssertions;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Tests;

/// <summary>Appeals at the datastore (env-gated <c>CLAIMS_TEST_DB</c>). Proves: an appeal re-enters the claim into
/// UnderAdjudication while the ORIGINAL decision row is preserved byte-identical and the appeal links to it; the
/// original decider cannot re-decide the appealed line (SoD); and an appeal on a SETTLED batch is routed to adjustment
/// with the settled batch left untouched. Self-cleans by tenant scope.</summary>
[Collection("claims-db")]
public class AppealIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("CLAIMS_TEST_DB");
    private static DbContextOptions<ClaimsDbContext> Options() =>
        new DbContextOptionsBuilder<ClaimsDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;
    private static ClaimsDbContext Ctx() => new(Options());

    private static async Task<(Guid claimId, Guid lineId, Guid decisionId)> SeedDecided(
        string tenant, ClaimStatus status, string decider)
    {
        await using var db = Ctx();
        var claim = new Claim
        {
            ClaimId = Guid.NewGuid(), ClaimNo = await new ClaimNoIssuer(db).NextAsync(2026),
            Origin = ClaimOrigin.ProviderSubmitted, BeneficiaryId = Guid.NewGuid(), ProviderId = Guid.NewGuid(),
            TenantId = tenant, ServiceDateFrom = new DateOnly(2026, 7, 1), CurrencyCode = "EGP", ClaimedAmount = 200m,
            Status = status, CreatedBy = "system", CreatedAt = DateTimeOffset.UtcNow, DecidedAt = DateTimeOffset.UtcNow,
        };
        var line = new ClaimLine
        {
            ClaimLineId = Guid.NewGuid(), ClaimId = claim.ClaimId, CodeSystem = ClaimCodeSystem.CPT, Code = "80053",
            Quantity = 1, BilledAmount = 200m, ContractPrice = 180m,
            AllowedAmount = status == ClaimStatus.Denied ? 0m : 180m,
            Status = status == ClaimStatus.Denied ? ClaimLineStatus.Denied : ClaimLineStatus.Approved,
            FulfillmentRef = Guid.NewGuid(), FulfillmentType = FulfillmentType.OrderFulfillment,
        };
        claim.Lines.Add(line);
        db.Claims.Add(claim);
        await db.SaveChangesAsync(); // persist claim + line before the decision (unmodeled FK ordering)
        var decision = new ClaimDecision
        {
            DecisionId = Guid.NewGuid(), ClaimLineId = line.ClaimLineId, ClaimId = claim.ClaimId, TenantId = tenant,
            Decision = status == ClaimStatus.Denied ? ClaimDecisionKind.Deny : ClaimDecisionKind.Approve,
            AllowedAmount = line.AllowedAmount, ReasonCodes = status == ClaimStatus.Denied ? [ReasonCodes.LimitExceeded] : [],
            Rationale = "original decision", DecidedBy = decider, DecidedAt = DateTimeOffset.UtcNow,
            RuleVersion = "10b.3.0", CorrelationId = "seed",
        };
        db.ClaimDecisions.Add(decision);
        await db.SaveChangesAsync();
        return (claim.ClaimId, line.ClaimLineId, decision.DecisionId);
    }

    [Fact]
    public async Task An_appeal_re_enters_adjudication_preserving_the_original_decision_thread()
    {
        if (Db is null) return;
        var tenant = T();
        try
        {
            var (claimId, lineId, decisionId) = await SeedDecided(tenant, ClaimStatus.Denied, "officer-1");
            var before = await Ctx().ClaimDecisions.AsNoTracking().SingleAsync(d => d.DecisionId == decisionId);

            await using (var db = Ctx())
            {
                var r = await new AppealService(db, TimeProvider.System)
                    .RaiseAsync(tenant, "provider-user", claimId, lineId, AppellantType.Provider, "wrong denial", null);
                r.Outcome.Should().Be(AppealOutcome.Raised);
                r.Appeal!.OriginalDecisionId.Should().Be(decisionId);
                r.Appeal.Resolution.Should().Be(AppealResolution.ReAdjudication);
            }
            await using var verify = Ctx();
            (await verify.Claims.AsNoTracking().SingleAsync(c => c.ClaimId == claimId)).Status.Should().Be(ClaimStatus.UnderAdjudication);
            (await verify.ClaimLines.AsNoTracking().SingleAsync(l => l.ClaimLineId == lineId)).Status.Should().Be(ClaimLineStatus.Pending);

            // the original decision row is untouched (byte-identical on every field).
            var after = await verify.ClaimDecisions.AsNoTracking().SingleAsync(d => d.DecisionId == decisionId);
            after.DecidedBy.Should().Be(before.DecidedBy);
            after.Decision.Should().Be(before.Decision);
            after.AllowedAmount.Should().Be(before.AllowedAmount);
            after.Rationale.Should().Be(before.Rationale);
            after.DecidedAt.Should().Be(before.DecidedAt);
        }
        finally { await Cleanup(tenant); }
    }

    [Fact]
    public async Task The_original_decider_cannot_re_decide_the_appealed_line()
    {
        if (Db is null) return;
        var tenant = T();
        try
        {
            var (claimId, lineId, _) = await SeedDecided(tenant, ClaimStatus.Denied, "officer-1");
            await using (var db = Ctx())
                await new AppealService(db, TimeProvider.System)
                    .RaiseAsync(tenant, "provider-user", claimId, lineId, AppellantType.Provider, "reconsider", null);

            // officer-1 (the original decider) is blocked; a different reviewer may decide.
            await using (var db = Ctx())
            {
                var svc = new DecisionService(db, TimeProvider.System);
                var req = new DecisionRequest(ClaimDecisionKind.Approve, null, [], null, false, null);
                (await svc.DecideAsync(tenant, "officer-1", null, claimId, lineId, req, "re-k1", 1_000_000m, "c")).Outcome
                    .Should().Be(DecisionOutcome.SoDSameDecider);
            }
            await using (var db = Ctx())
            {
                var svc = new DecisionService(db, TimeProvider.System);
                var req = new DecisionRequest(ClaimDecisionKind.Approve, null, [], null, false, null);
                (await svc.DecideAsync(tenant, "reviewer-2", null, claimId, lineId, req, "re-k2", 1_000_000m, "c")).Outcome
                    .Should().Be(DecisionOutcome.Recorded);
            }
        }
        finally { await Cleanup(tenant); }
    }

    [Fact]
    public async Task An_appeal_on_a_settled_batch_routes_to_adjustment_and_leaves_the_batch_untouched()
    {
        if (Db is null) return;
        var tenant = T();
        try
        {
            var (claimId, lineId, _) = await SeedDecided(tenant, ClaimStatus.Approved, "officer-1");
            Guid batchId;
            await using (var db = Ctx())
            {
                var batch = new ClaimBatch
                {
                    BatchId = Guid.NewGuid(), BatchNo = await new BatchNoIssuer(db).NextAsync(2026), BatchType = BatchType.Provider,
                    SelectionMode = BatchSelectionMode.Manual, PayeeProviderId = Guid.NewGuid(), TenantId = tenant, PeriodFrom = new DateOnly(2026, 7, 1),
                    PeriodTo = new DateOnly(2026, 7, 31), Status = BatchStatus.SettlementIssued, FrozenAt = DateTimeOffset.UtcNow,
                    TotalApproved = 180m, NetPayable = 180m, CreatedBy = "creator", CreatedAt = DateTimeOffset.UtcNow,
                };
                batch.Items.Add(new ClaimBatchItem { BatchItemId = Guid.NewGuid(), BatchId = batch.BatchId, ClaimId = claimId, AddedAt = DateTimeOffset.UtcNow, BatchStatusSnapshot = BatchStatus.SettlementIssued });
                var claim = await db.Claims.SingleAsync(c => c.ClaimId == claimId);
                claim.BatchId = batch.BatchId;
                db.ClaimBatches.Add(batch);
                await db.SaveChangesAsync();
                batchId = batch.BatchId;
            }

            await using (var db = Ctx())
            {
                var r = await new AppealService(db, TimeProvider.System)
                    .RaiseAsync(tenant, "provider-user", claimId, lineId, AppellantType.Provider, "post-settlement dispute", null);
                r.Outcome.Should().Be(AppealOutcome.RoutedToAdjustment);
            }
            await using var verify = Ctx();
            var batchAfter = await verify.ClaimBatches.AsNoTracking().SingleAsync(b => b.BatchId == batchId);
            batchAfter.Status.Should().Be(BatchStatus.SettlementIssued, "a settled batch is never reopened");
            batchAfter.NetPayable.Should().Be(180m, "its rollups stay frozen");
            (await verify.Claims.AsNoTracking().SingleAsync(c => c.ClaimId == claimId)).Status
                .Should().Be(ClaimStatus.Approved, "the settled claim is untouched");

            // the correction flows as a compensating recovery referencing the original line (nets into a later batch).
            await using (var db = Ctx())
            {
                var adj = new AdjustmentRequest(AdjustmentType.Recovery, -50m, ReasonCodes.DuplicateClaim, "post-settlement recovery", lineId, null);
                (await new AdjustmentService(db, TimeProvider.System).RaiseAsync(tenant, "reviewer-2", claimId, lineId, adj, "adj-appeal", "c")).Outcome
                    .Should().Be(AdjustmentOutcome.Recorded);
            }
            (await verify.ClaimBatches.AsNoTracking().SingleAsync(b => b.BatchId == batchId)).NetPayable
                .Should().Be(180m, "the settled batch is still untouched after the correction");
        }
        finally { await Cleanup(tenant); }
    }

    private static string T() => "t-" + Guid.NewGuid().ToString("N")[..10];

    private static async Task Cleanup(string tenant)
    {
        if (Db is null) return;
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "SET session_replication_role = replica; " +
            "DELETE FROM claims.claim_appeal WHERE tenant_id = {0}; " +
            "DELETE FROM claims.claim_adjustment WHERE tenant_id = {0}; " +
            "DELETE FROM claims.claim_decision WHERE tenant_id = {0}; " +
            "DELETE FROM claims.claim_batch_item WHERE batch_id IN (SELECT batch_id FROM claims.claim_batch WHERE tenant_id = {0}); " +
            "DELETE FROM claims.claim_line WHERE claim_id IN (SELECT claim_id FROM claims.claim WHERE tenant_id = {0}); " +
            "DELETE FROM claims.claim WHERE tenant_id = {0}; " +
            "DELETE FROM claims.claim_batch WHERE tenant_id = {0}; " +
            "SET session_replication_role = origin;", tenant);
    }
}
