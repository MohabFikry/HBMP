using FluentAssertions;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Tests;

/// <summary>
/// Phase 18.A2 / audit R2 X2, X3, X8 — the three money defects in the claims layer.
///
/// X2 the batch rollup erased applied adjustments at the freeze point; X3 the allowed-amount cap was
/// inverted (<c>Math.Max</c>), permitting approval above the contract tariff, with <c>Adjust</c>
/// uncapped entirely; X8 the contract price ignored line quantity, so a qty-3 line paid one unit and
/// reconciliation flagged every multi-unit line as a price variance.
/// </summary>
public class ClaimsMoneyLayerCapTests
{
    // ── X3: the cap is the LESSER of billed and the contract tariff ────────────────────────────────

    [Fact]
    public void Partial_above_the_contract_tariff_is_rejected()
    {
        var err = DecisionRules.Validate(ClaimDecisionKind.PartiallyApprove, allowed: 300m,
            reasonCodes: [ReasonCodes.NoTariff], rationale: "partial", billed: 500m, contractPrice: 100m, isOverride: false);

        err.Should().Be("allowed-exceeds-cap", "billed 500 / contract 100 caps payable at 100");
    }

    [Fact]
    public void An_Adjust_decision_above_the_cap_is_rejected()
    {
        // Adjust carried NO cap at all before 18.A2.
        var err = DecisionRules.Validate(ClaimDecisionKind.Adjust, allowed: 400m,
            reasonCodes: [], rationale: "re-price", billed: 500m, contractPrice: 100m, isOverride: false);

        err.Should().Be("allowed-exceeds-cap");
    }

    [Fact]
    public void An_Approve_above_the_contract_tariff_is_rejected()
    {
        var err = DecisionRules.Validate(ClaimDecisionKind.Approve, allowed: 500m,
            reasonCodes: [], rationale: null, billed: 500m, contractPrice: 100m, isOverride: false);

        err.Should().Be("allowed-exceeds-cap", "Math.Max used to permit paying the billed amount over tariff");
    }

    [Theory]
    [InlineData(ClaimDecisionKind.Approve)]
    [InlineData(ClaimDecisionKind.PartiallyApprove)]
    [InlineData(ClaimDecisionKind.Adjust)]
    public void No_decision_kind_can_apply_an_amount_above_the_cap(ClaimDecisionKind kind)
    {
        // Belt and braces: even a caller that bypasses Validate cannot write above the tariff.
        var effect = DecisionRules.Apply(kind, allowed: 9_999m, billed: 500m, contractPrice: 100m);

        effect.Should().NotBeNull();
        effect!.Value.Allowed.Should().Be(100m);
    }

    [Fact]
    public void Without_a_tariff_the_billed_amount_is_the_only_ceiling()
    {
        DecisionRules.Cap(billed: 500m, contractPrice: null).Should().Be(500m);
        DecisionRules.Validate(ClaimDecisionKind.Approve, 500m, [], null, 500m, null, false).Should().BeNull();
        DecisionRules.Validate(ClaimDecisionKind.Approve, 501m, [], null, 500m, null, false).Should().Be("allowed-exceeds-cap");
    }

    // ── X8: contract price is the EXTENDED price ───────────────────────────────────────────────────

    [Fact]
    public void Contract_price_scales_with_line_quantity()
    {
        var (price, recommendation, reasons) = AutoDerivePricing.Price(resolvedTariff: 100m, quantity: 3m);

        price.Should().Be(300m, "a qty-3 line at 100/unit is priced at 300, not 100");
        recommendation.Should().BeNull();
        reasons.Should().BeEmpty();
    }

    [Fact]
    public void A_multi_unit_line_priced_at_the_tariff_is_not_a_price_variance()
    {
        // Reconciliation compares extended price to extended price; before X8 every multi-unit line
        // was bucketed PriceVariance because it compared a billed TOTAL against a UNIT tariff.
        var (price, _, _) = AutoDerivePricing.Price(resolvedTariff: 100m, quantity: 3m);

        var bucket = ReconClassifier.Classify(new ReconInput(
            Billed: true, Delivered: true, IsDuplicate: false,
            BilledAmount: 300m, ContractPrice: price, BilledQuantity: 3m, DeliveredQuantity: 3m));

        bucket.Should().Be(ReconBucket.Matched);
    }

    [Fact]
    public void A_missing_tariff_is_still_never_guessed()
    {
        var (price, recommendation, reasons) = AutoDerivePricing.Price(resolvedTariff: null, quantity: 3m);

        price.Should().BeNull();
        recommendation.Should().Be(SystemRecommendation.RequiresManualReview);
        reasons.Should().Contain(ReasonCodes.NoTariff);
    }
}

/// <summary>X2 at the datastore: an applied adjustment must survive every later rollup, especially the
/// <c>→ Decided</c> transition that runs immediately before totals freeze at <c>SettlementIssued</c>.
/// Env-gated on <c>CLAIMS_TEST_DB</c>; serialized with the other claims-db suites.</summary>
[Collection("claims-db")]
public class ClaimsRollupSurvivalTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("CLAIMS_TEST_DB");
    private static DbContextOptions<ClaimsDbContext> Options() =>
        new DbContextOptionsBuilder<ClaimsDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;
    private static ClaimsDbContext Ctx() => new(Options());

    [SkippableFact]
    public async Task Batch_totalAdjusted_survives_the_transition_to_Decided()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var provider = Guid.NewGuid();
        try
        {
            var (claimId, lineId) = await Seed(tenant, provider, approved: 500m);
            var period = new DateOnly(2026, 7, 1);
            Guid batchId;

            await using (var db = Ctx())
            {
                var batches = new BatchService(db, new BatchNoIssuer(db), new BatchRollupService(db), TimeProvider.System);
                var created = await batches.CreateAsync(tenant, "officer-1",
                    new BatchSelector(BatchType.Provider, BatchSelectionMode.Manual, provider, null, null,
                        period, period, null, null, [claimId]), default);
                created.Outcome.Should().Be(BatchOutcome.Ok);
                batchId = created.Batch!.BatchId;
            }

            // A -50 contractual deduction on the batched claim.
            await using (var db = Ctx())
            {
                var adjustments = new AdjustmentService(db, new BatchRollupService(db), TimeProvider.System);
                var r = await adjustments.RaiseAsync(tenant, "officer-1", claimId, lineId,
                    new AdjustmentRequest(AdjustmentType.Deduction, -50m, ReasonCodes.DuplicateClaim, "SLA penalty", null, null),
                    $"adj-x2-{tenant}", "corr");
                r.Outcome.Should().Be(AdjustmentOutcome.Recorded);
            }

            (await Batch(batchId)).TotalAdjusted.Should().Be(-50m);
            (await Batch(batchId)).NetPayable.Should().Be(450m);

            // The transitions that used to erase it: → UnderReview, → Decided (immediately before freeze).
            await using (var db = Ctx())
            {
                var batches = new BatchService(db, new BatchNoIssuer(db), new BatchRollupService(db), TimeProvider.System);
                (await batches.TransitionAsync(tenant, batchId, BatchStatus.UnderReview, null, default)).Outcome.Should().Be(BatchOutcome.Ok);
                (await batches.TransitionAsync(tenant, batchId, BatchStatus.Decided, null, default)).Outcome.Should().Be(BatchOutcome.Ok);
                (await batches.TransitionAsync(tenant, batchId, BatchStatus.SettlementIssued, null, default)).Outcome.Should().Be(BatchOutcome.Ok);
            }

            var settled = await Batch(batchId);
            settled.FrozenAt.Should().NotBeNull();
            settled.TotalApproved.Should().Be(500m);
            settled.TotalAdjusted.Should().Be(-50m, "the deduction must survive the freeze");
            settled.NetPayable.Should().Be(450m, "net payable = approved + adjustments (36 §8)");
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Recomputing_twice_does_not_double_count_an_adjustment()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var provider = Guid.NewGuid();
        try
        {
            var (claimId, lineId) = await Seed(tenant, provider, approved: 500m);
            var period = new DateOnly(2026, 7, 1);
            Guid batchId;
            await using (var db = Ctx())
            {
                var batches = new BatchService(db, new BatchNoIssuer(db), new BatchRollupService(db), TimeProvider.System);
                batchId = (await batches.CreateAsync(tenant, "o",
                    new BatchSelector(BatchType.Provider, BatchSelectionMode.Manual, provider, null, null,
                        period, period, null, null, [claimId]), default)).Batch!.BatchId;
            }
            await using (var db = Ctx())
            {
                await new AdjustmentService(db, new BatchRollupService(db), TimeProvider.System).RaiseAsync(
                    tenant, "o", claimId, lineId,
                    new AdjustmentRequest(AdjustmentType.Deduction, -50m, ReasonCodes.DuplicateClaim, "penalty", null, null),
                    $"adj-idem-{tenant}", "corr");
            }

            // Rollups are idempotent because approved (decided amounts) and adjusted (deltas) are disjoint.
            for (var i = 0; i < 3; i++)
            {
                await using var db = Ctx();
                var batch = await db.ClaimBatches.Include(b => b.Items).SingleAsync(b => b.BatchId == batchId);
                await new BatchRollupService(db).RecomputeAsync(batch, default);
                await db.SaveChangesAsync();
            }

            var b2 = await Batch(batchId);
            b2.TotalAdjusted.Should().Be(-50m);
            b2.NetPayable.Should().Be(450m);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task An_adjustment_never_rewrites_the_decided_allowed_amount()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            var (claimId, lineId) = await Seed(tenant, Guid.NewGuid(), approved: 500m);
            await using (var db = Ctx())
            {
                var r = await new AdjustmentService(db, new BatchRollupService(db), TimeProvider.System).RaiseAsync(
                    tenant, "o", claimId, lineId,
                    new AdjustmentRequest(AdjustmentType.Deduction, -50m, ReasonCodes.DuplicateClaim, "penalty", null, null),
                    $"adj-allowed-{tenant}", "corr");
                r.Outcome.Should().Be(AdjustmentOutcome.Recorded);
                // The ledger records the true payable movement…
                r.Adjustment!.BeforeAmount.Should().Be(500m);
                r.Adjustment.AfterAmount.Should().Be(450m);
            }

            await using var verify = Ctx();
            var line = await verify.ClaimLines.AsNoTracking().SingleAsync(l => l.ClaimLineId == lineId);
            // …and the decided amount is untouched, so the two can never disagree about the same money.
            line.AllowedAmount.Should().Be(500m);
            line.Status.Should().Be(ClaimLineStatus.Adjusted);
        }
        finally { await Cleanup(tenant); }
    }

    private static async Task<ClaimBatch> Batch(Guid batchId)
    {
        await using var db = Ctx();
        return await db.ClaimBatches.AsNoTracking().SingleAsync(b => b.BatchId == batchId);
    }

    private static async Task<(Guid ClaimId, Guid LineId)> Seed(string tenant, Guid provider, decimal approved)
    {
        await using var db = Ctx();
        var claim = new Claim
        {
            ClaimId = Guid.NewGuid(), ClaimNo = await new ClaimNoIssuer(db).NextAsync(2026),
            Origin = ClaimOrigin.AutoDerived, BeneficiaryId = Guid.NewGuid(), ProviderId = provider,
            TenantId = tenant, ServiceDateFrom = new DateOnly(2026, 7, 1), ServiceDateTo = new DateOnly(2026, 7, 1),
            CurrencyCode = "EGP", ClaimedAmount = 500, PricedAmount = 500, Status = ClaimStatus.UnderAdjudication,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        claim.Lines.Add(new ClaimLine
        {
            ClaimLineId = Guid.NewGuid(), ClaimId = claim.ClaimId, CodeSystem = ClaimCodeSystem.CPT, Code = "80053",
            Quantity = 1, BilledAmount = 500, ContractPrice = 500, AllowedAmount = approved,
            Status = ClaimLineStatus.Approved, FulfillmentType = FulfillmentType.None,
        });
        db.Claims.Add(claim);
        await db.SaveChangesAsync();
        return (claim.ClaimId, claim.Lines[0].ClaimLineId);
    }

    private static async Task Cleanup(string tenant)
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = Ctx();
        // claim_adjustment is append-only (trigger blocks DELETE); disable user triggers for cleanup only.
        await db.Database.ExecuteSqlRawAsync(
            "SET session_replication_role = replica; " +
            "DELETE FROM claims.claim_adjustment WHERE tenant_id = {0}; " +
            "DELETE FROM claims.claim_batch_item WHERE claim_id IN (SELECT claim_id FROM claims.claim WHERE tenant_id = {0}); " +
            "DELETE FROM claims.claim_line WHERE claim_id IN (SELECT claim_id FROM claims.claim WHERE tenant_id = {0}); " +
            "DELETE FROM claims.claim WHERE tenant_id = {0}; " +
            "DELETE FROM claims.claim_batch WHERE tenant_id = {0}; " +
            "SET session_replication_role = origin;", tenant);
    }
}
