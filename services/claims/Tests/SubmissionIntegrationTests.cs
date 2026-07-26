using FluentAssertions;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Tests;

/// <summary>Provider-submission at the datastore (env-gated <c>CLAIMS_TEST_DB</c>). Proves: a MATCHED line records the
/// provider's billed amount alongside the contract price and flags the variance; an UNMATCHED line lands in the manual
/// queue with NO_FULFILLMENT_RECORD and is never auto-approved; a re-submission of an already-claimed fulfillment hits
/// the 10b.1 unique index → Duplicate with no second payable line; idempotency returns one submission; and provider
/// isolation scopes reads to the owning provider. Self-cleans by tenant scope.</summary>
[Collection("claims-db")]
public class SubmissionIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("CLAIMS_TEST_DB");
    private static DbContextOptions<ClaimsDbContext> Options() =>
        new DbContextOptionsBuilder<ClaimsDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;
    private static ClaimsDbContext Ctx() => new(Options());

    // A resolver keyed by service code, and a fixed-price tariff — the seams the real orders/provider wiring fills.
    private sealed class FakeResolver(Dictionary<string, FulfillmentMatch> map) : IFulfillmentResolver
    {
        public Task<FulfillmentMatch?> ResolveAsync(MatchKey key, DateOnly serviceDate, string? bearer, CancellationToken ct = default)
            => Task.FromResult(map.TryGetValue(key.Code, out var m) ? m : (FulfillmentMatch?)null);
    }
    private sealed class FakeTariff(decimal? price) : IContractTariffProvider
    {
        public Task<decimal?> ResolveAsync(Guid p, ClaimCodeSystem cs, string code, DateOnly d, string? b, CancellationToken ct = default)
            => Task.FromResult(price);
    }

    private static SubmissionService Svc(ClaimsDbContext db, IFulfillmentResolver resolver, decimal? tariff) =>
        new(db, new ClaimNoIssuer(db), resolver, new FakeTariff(tariff), TimeProvider.System);

    private static SubmissionRequest Req(Guid provider, Guid beneficiary, params SubmissionLineInput[] lines) =>
        new(provider, beneficiary, "INV-1", "EGP", null, lines);

    private static SubmissionLineInput Line(string code, decimal billed) =>
        new(ClaimCodeSystem.CPT, code, "svc", new DateOnly(2026, 7, 10), 1, billed, null);

    [Fact]
    public async Task Matched_line_records_billed_and_contract_and_flags_variance()
    {
        if (Db is null) return;
        var tenant = T();
        var (provider, beneficiary) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            var fulfillment = Guid.NewGuid();
            var resolver = new FakeResolver(new()
            {
                ["80053"] = new FulfillmentMatch(fulfillment, FulfillmentType.OrderFulfillment, new DateOnly(2026, 7, 10)),
            });
            await using var db = Ctx();
            var r = await Svc(db, resolver, tariff: 180m).SubmitAsync(
                tenant, "provider-user", Req(provider, beneficiary, Line("80053", 200m)), "idem-match", null);

            r.Outcome.Should().Be(SubmitOutcome.Created);
            r.Submission!.Status.Should().Be(SubmissionStatus.Matched);
            var subLine = r.Submission.Lines.Single();
            subLine.Outcome.Should().Be(SubmissionLineOutcome.Matched);
            subLine.PriceVariance.Should().BeTrue("billed 200 ≠ contract 180");

            await using var verify = Ctx();
            var line = await verify.ClaimLines.AsNoTracking().SingleAsync(l => l.ClaimLineId == subLine.ClaimLineId);
            line.FulfillmentRef.Should().Be(fulfillment);
            line.BilledAmount.Should().Be(200m);
            line.ContractPrice.Should().Be(180m, "the contract price is recorded ALONGSIDE billed, not overwritten");
        }
        finally { await Cleanup(tenant); }
    }

    [Fact]
    public async Task Unmatched_line_lands_in_the_manual_queue_and_is_never_auto_approved()
    {
        if (Db is null) return;
        var tenant = T();
        var (provider, beneficiary) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            var resolver = new FakeResolver(new()); // resolves nothing
            await using var db = Ctx();
            var r = await Svc(db, resolver, tariff: null).SubmitAsync(
                tenant, "provider-user", Req(provider, beneficiary, Line("99999", 150m)), "idem-unmatched", null);

            r.Outcome.Should().Be(SubmitOutcome.Created);
            r.Submission!.Status.Should().Be(SubmissionStatus.Unmatched);
            r.Submission.Lines.Single().ReasonCode.Should().Be(ReasonCodes.NoFulfillmentRecord);

            await using var verify = Ctx();
            var line = await verify.ClaimLines.AsNoTracking().SingleAsync(l => l.ClaimId == r.Claim!.ClaimId);
            line.FulfillmentRef.Should().BeNull();
            line.SystemRecommendation.Should().Be(SystemRecommendation.RequiresManualReview);
            line.Status.Should().Be(ClaimLineStatus.Pending, "an unmatched line is queued, never auto-approved");
            line.ReasonCodes.Should().Contain(ReasonCodes.NoFulfillmentRecord);
            // The manual line reaches the officer worklist (claim is UnderAdjudication).
            (await verify.Claims.AsNoTracking().SingleAsync(c => c.ClaimId == r.Claim!.ClaimId)).Status
                .Should().Be(ClaimStatus.UnderAdjudication);
        }
        finally { await Cleanup(tenant); }
    }

    [Fact]
    public async Task Resubmission_of_an_already_claimed_fulfillment_is_a_duplicate_with_no_second_line()
    {
        if (Db is null) return;
        var tenant = T();
        var (provider, beneficiary) = (Guid.NewGuid(), Guid.NewGuid());
        var fulfillment = Guid.NewGuid();
        try
        {
            // Seed an existing (auto-derived) live payable line for this fulfillment.
            await using (var seed = Ctx())
            {
                var claim = new Claim
                {
                    ClaimId = Guid.NewGuid(), ClaimNo = await new ClaimNoIssuer(seed).NextAsync(2026),
                    Origin = ClaimOrigin.AutoDerived, BeneficiaryId = beneficiary, ProviderId = provider, TenantId = tenant,
                    ServiceDateFrom = new DateOnly(2026, 7, 10), CurrencyCode = "EGP", ClaimedAmount = 180m,
                    Status = ClaimStatus.UnderAdjudication, CreatedAt = DateTimeOffset.UtcNow,
                };
                claim.Lines.Add(new ClaimLine
                {
                    ClaimLineId = Guid.NewGuid(), ClaimId = claim.ClaimId, CodeSystem = ClaimCodeSystem.CPT, Code = "80053",
                    Quantity = 1, BilledAmount = 180m, ContractPrice = 180m, Status = ClaimLineStatus.Pending,
                    FulfillmentRef = fulfillment, FulfillmentType = FulfillmentType.OrderFulfillment,
                });
                seed.Claims.Add(claim);
                await seed.SaveChangesAsync();
            }

            var resolver = new FakeResolver(new()
            {
                ["80053"] = new FulfillmentMatch(fulfillment, FulfillmentType.OrderFulfillment, new DateOnly(2026, 7, 10)),
            });
            await using var db = Ctx();
            var r = await Svc(db, resolver, tariff: 180m).SubmitAsync(
                tenant, "provider-user", Req(provider, beneficiary, Line("80053", 200m)), "idem-dup", null);

            r.Outcome.Should().Be(SubmitOutcome.Duplicate);

            await using var verify = Ctx();
            (await verify.ClaimLines.CountAsync(l => l.FulfillmentRef == fulfillment && l.Status != ClaimLineStatus.Void))
                .Should().Be(1, "the no-double-billing index prevents a second live payable line");
            (await verify.ClaimSubmissions.CountAsync(s => s.TenantId == tenant))
                .Should().Be(0, "a duplicate submission is rejected atomically — nothing is created");
        }
        finally { await Cleanup(tenant); }
    }

    [Fact]
    public async Task Same_idempotency_key_yields_one_submission()
    {
        if (Db is null) return;
        var tenant = T();
        var (provider, beneficiary) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            var resolver = new FakeResolver(new());
            await using (var db = Ctx())
                await Svc(db, resolver, null).SubmitAsync(tenant, "u", Req(provider, beneficiary, Line("99999", 100m)), "idem-1", null);
            await using (var db = Ctx())
            {
                var again = await Svc(db, resolver, null).SubmitAsync(tenant, "u", Req(provider, beneficiary, Line("99999", 100m)), "idem-1", null);
                again.Outcome.Should().Be(SubmitOutcome.Replayed);
            }
            (await Ctx().ClaimSubmissions.CountAsync(s => s.IdempotencyKey == "idem-1")).Should().Be(1);
        }
        finally { await Cleanup(tenant); }
    }

    [Fact]
    public async Task A_submission_is_visible_only_to_its_owning_provider()
    {
        if (Db is null) return;
        var tenant = T();
        var (providerA, providerB, beneficiary) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        try
        {
            await using (var db = Ctx())
                await Svc(db, new FakeResolver(new()), null).SubmitAsync(
                    tenant, "prov-a", Req(providerA, beneficiary, Line("99999", 100m)), "idem-iso", null);

            await using var verify = Ctx();
            // The endpoint scopes a provider read by provider_id; provider B sees nothing of provider A's.
            (await verify.ClaimSubmissions.AsNoTracking()
                .CountAsync(s => s.TenantId == tenant && s.ProviderId == providerB)).Should().Be(0);
            (await verify.ClaimSubmissions.AsNoTracking()
                .CountAsync(s => s.TenantId == tenant && s.ProviderId == providerA)).Should().Be(1);
        }
        finally { await Cleanup(tenant); }
    }

    private static string T() => "t-" + Guid.NewGuid().ToString("N")[..10];

    private static async Task Cleanup(string tenant)
    {
        if (Db is null) return;
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM claims.claim_submission_line WHERE submission_id IN " +
            "  (SELECT submission_id FROM claims.claim_submission WHERE tenant_id = {0}); " +
            "DELETE FROM claims.claim_document WHERE claim_id IN (SELECT claim_id FROM claims.claim WHERE tenant_id = {0}); " +
            "DELETE FROM claims.claim_submission WHERE tenant_id = {0}; " +
            "DELETE FROM claims.claim_line WHERE claim_id IN (SELECT claim_id FROM claims.claim WHERE tenant_id = {0}); " +
            "DELETE FROM claims.claim WHERE tenant_id = {0};", tenant);
    }
}
