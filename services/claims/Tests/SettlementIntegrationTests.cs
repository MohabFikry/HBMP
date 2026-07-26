using System.Text;
using FluentAssertions;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Tests;

/// <summary>Settlement advice + exports at the datastore (env-gated <c>CLAIMS_TEST_DB</c>). Proves: a Decided batch
/// generates an immutable advice (append-only row + content hash + document ref), references it, freezes the rollups,
/// and moves the batch to SettlementIssued; regeneration writes a NEW version preserving the old; an external payment
/// reference records a fact and closes the batch; and the advice row is append-only. Self-cleans by tenant scope.</summary>
[Collection("claims-db")]
public class SettlementIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("CLAIMS_TEST_DB");
    private static DbContextOptions<ClaimsDbContext> Options() =>
        new DbContextOptionsBuilder<ClaimsDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;
    private static ClaimsDbContext Ctx() => new(Options());
    private static SettlementService Svc(ClaimsDbContext db) => new(db, new NullWormStore(), TimeProvider.System);

    private static async Task<Guid> SeedDecidedBatch(string tenant, Guid provider)
    {
        await using var db = Ctx();
        var claim = new Claim
        {
            ClaimId = Guid.NewGuid(), ClaimNo = await new ClaimNoIssuer(db).NextAsync(2026),
            Origin = ClaimOrigin.ProviderSubmitted, BeneficiaryId = Guid.NewGuid(), ProviderId = provider, TenantId = tenant,
            ServiceDateFrom = new DateOnly(2026, 7, 1), CurrencyCode = "EGP", ClaimedAmount = 200m,
            Status = ClaimStatus.Approved, CreatedBy = "system", CreatedAt = DateTimeOffset.UtcNow,
        };
        claim.Lines.Add(new ClaimLine
        {
            ClaimLineId = Guid.NewGuid(), ClaimId = claim.ClaimId, CodeSystem = ClaimCodeSystem.CPT, Code = "80053",
            Quantity = 1, BilledAmount = 200m, ContractPrice = 180m, AllowedAmount = 180m, Status = ClaimLineStatus.Approved,
            FulfillmentRef = Guid.NewGuid(), FulfillmentType = FulfillmentType.OrderFulfillment,
        });
        db.Claims.Add(claim);

        var batch = new ClaimBatch
        {
            BatchId = Guid.NewGuid(), BatchNo = await new BatchNoIssuer(db).NextAsync(2026), BatchType = BatchType.Provider,
            SelectionMode = BatchSelectionMode.Manual, PayeeProviderId = provider, TenantId = tenant,
            PeriodFrom = new DateOnly(2026, 7, 1), PeriodTo = new DateOnly(2026, 7, 31), Status = BatchStatus.Decided,
            TotalApproved = 180m, NetPayable = 180m, CreatedBy = "creator", CreatedAt = DateTimeOffset.UtcNow,
        };
        batch.Items.Add(new ClaimBatchItem
        {
            BatchItemId = Guid.NewGuid(), BatchId = batch.BatchId, ClaimId = claim.ClaimId, AddedBy = "creator",
            AddedAt = DateTimeOffset.UtcNow, BatchStatusSnapshot = BatchStatus.Decided,
        });
        claim.BatchId = batch.BatchId;
        db.ClaimBatches.Add(batch);
        await db.SaveChangesAsync();
        return batch.BatchId;
    }

    // ── 18.A4 — segregation of duties + frozen regeneration ───────────────────────────────────────

    [SkippableFact]
    public async Task The_batch_creator_may_not_release_its_own_settlement()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        try
        {
            var batchId = await SeedDecidedBatch(tenant, Guid.NewGuid());   // seeded with CreatedBy = "creator"

            await using var db = Ctx();
            // Release is the last human control before money moves on the strength of this document, so one
            // actor doing both is the classic single-point fraud path (36 §9).
            var r = await Svc(db).GenerateAsync(tenant, batchId, "creator", null);

            r.Outcome.Should().Be(SettlementOutcome.SoDSameActor);
            r.Advice.Should().BeNull();

            await using var verify = Ctx();
            (await verify.ClaimBatches.AsNoTracking().SingleAsync(b => b.BatchId == batchId)).Status
                .Should().Be(BatchStatus.Decided, "a refused release must not move the batch");
            (await verify.SettlementAdvices.CountAsync(a => a.BatchId == batchId)).Should().Be(0);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task A_regenerated_advice_reproduces_the_frozen_figures()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        var provider = Guid.NewGuid();
        try
        {
            var batchId = await SeedDecidedBatch(tenant, provider);
            await using (var db = Ctx()) await Svc(db).GenerateAsync(tenant, batchId, "releaser", null);

            // Mutate a line AFTER settlement. Regeneration used to rebuild from live rows, so version 2
            // could quietly disagree with the version already sent to the provider. Corrections belong in
            // a NEW batch (23 §9).
            await using (var db = Ctx())
            {
                var line = await db.ClaimLines.FirstAsync(l => l.BilledAmount == 200m && l.AllowedAmount == 180m);
                line.AllowedAmount = 5m;
                await db.SaveChangesAsync();
            }

            await using (var db = Ctx())
            {
                var again = await Svc(db).GenerateAsync(tenant, batchId, "releaser", null);
                again.Outcome.Should().Be(SettlementOutcome.Regenerated);
                again.Advice!.Version.Should().Be(2);
                again.Advice.TotalApproved.Should().Be(180m, "the frozen figure, not the mutated live row");
                again.Advice.NetPayable.Should().Be(180m);
            }
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Decided_batch_generates_an_immutable_frozen_settlement_advice()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        var provider = Guid.NewGuid();
        try
        {
            var batchId = await SeedDecidedBatch(tenant, provider);
            Guid adviceId;
            await using (var db = Ctx())
            {
                var r = await Svc(db).GenerateAsync(tenant, batchId, "releaser", null);
                r.Outcome.Should().Be(SettlementOutcome.Generated);
                r.Advice!.Version.Should().Be(1);
                r.Advice.ContentHash.Should().NotBeNullOrEmpty();
                r.Advice.DocumentId.Should().NotBeNull();
                r.Advice.NetPayable.Should().Be(180m);
                r.Batch!.Status.Should().Be(BatchStatus.SettlementIssued);
                r.Batch.FrozenAt.Should().NotBeNull("the rollups are frozen at SettlementIssued");
                adviceId = r.Advice.AdviceId;
            }
            await using var verify = Ctx();
            var batch = await verify.ClaimBatches.AsNoTracking().SingleAsync(b => b.BatchId == batchId);
            batch.SettlementDocumentId.Should().NotBeNull();

            // append-only: a direct UPDATE on the advice ledger is rejected by the trigger.
            var act = async () => await verify.Database.ExecuteSqlRawAsync(
                "UPDATE claims.settlement_advice SET net_payable = 0 WHERE advice_id = {0}", adviceId);
            await act.Should().ThrowAsync<Exception>();
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Regeneration_writes_a_new_version_and_preserves_the_old()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        var provider = Guid.NewGuid();
        try
        {
            var batchId = await SeedDecidedBatch(tenant, provider);
            await using (var db = Ctx()) await Svc(db).GenerateAsync(tenant, batchId, "releaser", null);
            await using (var db = Ctx())
            {
                var r = await Svc(db).GenerateAsync(tenant, batchId, "releaser", null);
                r.Outcome.Should().Be(SettlementOutcome.Regenerated);
                r.Advice!.Version.Should().Be(2);
                r.Advice.SupersedesAdviceId.Should().NotBeNull();
            }
            (await Ctx().SettlementAdvices.CountAsync(a => a.BatchId == batchId)).Should().Be(2, "the old advice is preserved");
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task An_export_is_provider_isolated_and_carries_no_clinical_field()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        var provider = Guid.NewGuid();
        try
        {
            var batchId = await SeedDecidedBatch(tenant, provider);
            await using var db = Ctx();
            // a DIFFERENT provider is denied.
            (await Svc(db).ExportAsync(tenant, batchId, "CSV", Guid.NewGuid().ToString(), "prov")).Outcome
                .Should().Be(ExportOutcome.ProviderDenied);
            // the owning provider gets a clinical-free file.
            var mine = await Svc(db).ExportAsync(tenant, batchId, "CSV", provider.ToString(), "prov");
            mine.Outcome.Should().Be(ExportOutcome.Ok);
            var text = Encoding.UTF8.GetString(mine.File!.Bytes).ToLowerInvariant();
            text.Should().NotContain("diagnosis").And.NotContain("icd");
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task An_external_payment_reference_records_a_fact_and_closes_the_batch()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        var provider = Guid.NewGuid();
        try
        {
            var batchId = await SeedDecidedBatch(tenant, provider);
            await using (var db = Ctx()) await Svc(db).GenerateAsync(tenant, batchId, "releaser", null);
            await using (var db = Ctx())
                (await Svc(db).RecordPaymentReferenceAsync(tenant, batchId, "TT-99887", new DateOnly(2026, 8, 1), "finance"))
                    .Should().Be(PaymentRefOutcome.Recorded);
            (await Ctx().ClaimBatches.AsNoTracking().SingleAsync(b => b.BatchId == batchId)).Status
                .Should().Be(BatchStatus.Closed);
            (await Ctx().SettlementPaymentReferences.CountAsync(p => p.BatchId == batchId)).Should().Be(1);
        }
        finally { await Cleanup(tenant); }
    }

    private static string T() => "t-" + Guid.NewGuid().ToString("N")[..10];

    private static async Task Cleanup(string tenant)
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = Ctx();
        // settlement_advice + settlement_payment_reference are append-only (trigger blocks DELETE).
        await db.Database.ExecuteSqlRawAsync(
            "SET session_replication_role = replica; " +
            "DELETE FROM claims.settlement_payment_reference WHERE tenant_id = {0}; " +
            "DELETE FROM claims.settlement_advice WHERE tenant_id = {0}; " +
            "DELETE FROM claims.claim_batch_item WHERE batch_id IN (SELECT batch_id FROM claims.claim_batch WHERE tenant_id = {0}); " +
            "DELETE FROM claims.claim_line WHERE claim_id IN (SELECT claim_id FROM claims.claim WHERE tenant_id = {0}); " +
            "DELETE FROM claims.claim WHERE tenant_id = {0}; " +
            "DELETE FROM claims.claim_batch WHERE tenant_id = {0}; " +
            "SET session_replication_role = origin;", tenant);
    }
}
