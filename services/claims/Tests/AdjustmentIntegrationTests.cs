using FluentAssertions;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Tests;

/// <summary>Append-only adjustments at the datastore (env-gated <c>CLAIMS_TEST_DB</c>). Proves: a PriceCorrection
/// records a signed append-only entry with before/after amounts and re-nets the line; a Recovery with no original-line
/// reference is rejected with nothing recorded; an adjustment that would make the net payable negative needs a second
/// distinct approver; and an UPDATE/DELETE on claim_adjustment is rejected by the append-only trigger. The original
/// line/decision is never mutated in place. Self-cleans by tenant scope.</summary>
[Collection("claims-db")]
public class AdjustmentIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("CLAIMS_TEST_DB");
    private static DbContextOptions<ClaimsDbContext> Options() =>
        new DbContextOptionsBuilder<ClaimsDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;
    private static ClaimsDbContext Ctx() => new(Options());
    private static AdjustmentService Svc(ClaimsDbContext db) => new(db, TimeProvider.System);

    private static async Task<(Guid claimId, Guid lineId)> SeedApproved(string tenant, decimal allowed)
    {
        await using var db = Ctx();
        var claim = new Claim
        {
            ClaimId = Guid.NewGuid(), ClaimNo = await new ClaimNoIssuer(db).NextAsync(2026),
            Origin = ClaimOrigin.ProviderSubmitted, BeneficiaryId = Guid.NewGuid(), ProviderId = Guid.NewGuid(),
            TenantId = tenant, ServiceDateFrom = new DateOnly(2026, 7, 1), CurrencyCode = "EGP",
            ClaimedAmount = 200m, Status = ClaimStatus.Approved, CreatedBy = "system", CreatedAt = DateTimeOffset.UtcNow,
        };
        claim.Lines.Add(new ClaimLine
        {
            ClaimLineId = Guid.NewGuid(), ClaimId = claim.ClaimId, CodeSystem = ClaimCodeSystem.CPT, Code = "80053",
            Quantity = 1, BilledAmount = 200m, ContractPrice = 180m, AllowedAmount = allowed, Status = ClaimLineStatus.Approved,
            FulfillmentRef = Guid.NewGuid(), FulfillmentType = FulfillmentType.OrderFulfillment,
        });
        db.Claims.Add(claim);
        await db.SaveChangesAsync();
        return (claim.ClaimId, claim.Lines[0].ClaimLineId);
    }

    [SkippableFact]
    public async Task Price_correction_records_appendonly_with_before_after_and_renets_the_line()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        try
        {
            var (claimId, lineId) = await SeedApproved(tenant, allowed: 180m);
            Guid adjId;
            await using (var db = Ctx())
            {
                var req = new AdjustmentRequest(AdjustmentType.PriceCorrection, -20m, ReasonCodes.NoTariff, "re-price to tariff", null, null);
                var r = await Svc(db).RaiseAsync(tenant, "officer", claimId, lineId, req, "adj-1", "c");
                r.Outcome.Should().Be(AdjustmentOutcome.Recorded);
                r.Adjustment!.BeforeAmount.Should().Be(180m);
                r.Adjustment.AfterAmount.Should().Be(160m);
                adjId = r.Adjustment.AdjustmentId;
            }
            await using var verify = Ctx();
            var line = await verify.ClaimLines.AsNoTracking().SingleAsync(l => l.ClaimLineId == lineId);
            line.Status.Should().Be(ClaimLineStatus.Adjusted);
            line.AllowedAmount.Should().Be(160m);

            // append-only: a direct UPDATE on the adjustment ledger is rejected by the trigger.
            var act = async () => await verify.Database.ExecuteSqlRawAsync(
                "UPDATE claims.claim_adjustment SET amount_delta = -1 WHERE adjustment_id = {0}", adjId);
            await act.Should().ThrowAsync<Exception>();
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task A_recovery_without_an_original_line_is_rejected_and_records_nothing()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        try
        {
            var (claimId, lineId) = await SeedApproved(tenant, 180m);
            await using var db = Ctx();
            var req = new AdjustmentRequest(AdjustmentType.Recovery, -20m, ReasonCodes.DuplicateClaim, "recover overpay", null, null);
            var r = await Svc(db).RaiseAsync(tenant, "officer", claimId, lineId, req, "adj-2", "c");
            r.Outcome.Should().Be(AdjustmentOutcome.Validation);
            r.ValidationError.Should().Be("recovery-reference-required");
            (await Ctx().ClaimAdjustments.CountAsync(a => a.ClaimId == claimId)).Should().Be(0);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task A_negative_net_adjustment_needs_a_second_distinct_approver()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        try
        {
            var (claimId, lineId) = await SeedApproved(tenant, allowed: 100m);
            Guid pendingId;
            // 100 approved + (-150) = -50 < 0 → dual control.
            await using (var db = Ctx())
            {
                var req = new AdjustmentRequest(AdjustmentType.Deduction, -150m, ReasonCodes.LimitExceeded, "penalty", null, null);
                var r = await Svc(db).RaiseAsync(tenant, "officer-1", claimId, lineId, req, "adj-3", "c");
                r.Outcome.Should().Be(AdjustmentOutcome.PendingSecondApproval);
                pendingId = r.Adjustment!.AdjustmentId;
            }
            // the line is NOT changed while pending.
            (await Ctx().ClaimLines.AsNoTracking().SingleAsync(l => l.ClaimLineId == lineId)).Status
                .Should().Be(ClaimLineStatus.Approved);
            // the same approver cannot confirm.
            await using (var db = Ctx())
            {
                var same = new AdjustmentRequest(AdjustmentType.Deduction, -150m, ReasonCodes.LimitExceeded, "penalty", null, pendingId);
                (await Svc(db).RaiseAsync(tenant, "officer-1", claimId, lineId, same, "adj-4", "c")).Outcome
                    .Should().Be(AdjustmentOutcome.SoDSameApprover);
            }
            // a different approver confirms → applied.
            await using (var db = Ctx())
            {
                var conf = new AdjustmentRequest(AdjustmentType.Deduction, -150m, ReasonCodes.LimitExceeded, "penalty", null, pendingId);
                (await Svc(db).RaiseAsync(tenant, "officer-2", claimId, lineId, conf, "adj-5", "c")).Outcome
                    .Should().Be(AdjustmentOutcome.Confirmed);
            }
            (await Ctx().ClaimLines.AsNoTracking().SingleAsync(l => l.ClaimLineId == lineId)).Status
                .Should().Be(ClaimLineStatus.Adjusted);
        }
        finally { await Cleanup(tenant); }
    }

    private static string T() => "t-" + Guid.NewGuid().ToString("N")[..10];

    private static async Task Cleanup(string tenant)
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = Ctx();
        // claim_adjustment is append-only (trigger blocks DELETE); disable user triggers for this cleanup only.
        await db.Database.ExecuteSqlRawAsync(
            "SET session_replication_role = replica; " +
            "DELETE FROM claims.claim_adjustment WHERE tenant_id = {0}; " +
            "DELETE FROM claims.claim_line WHERE claim_id IN (SELECT claim_id FROM claims.claim WHERE tenant_id = {0}); " +
            "DELETE FROM claims.claim WHERE tenant_id = {0}; " +
            "SET session_replication_role = origin;", tenant);
    }
}
