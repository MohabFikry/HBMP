using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Claims.Api;
using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Tests;

/// <summary>
/// The rest of the provider portal's read surface — 11-permission-matrix §3.4, Provider Admin row:
/// <c>claim_adjustment R🔒🟠PO</c> and <c>settlement_advice R🔒🟠PO (own advice) E🔒🟠PO</c>.
///
/// <para>Both were the same finding as the claim read: the isolation was written and the authority was not.
/// <c>SettlementService.ExportAsync</c> carries a whole <c>ProviderDenied</c> branch — with a High-severity
/// EXPORT_CROSS_PROVIDER audit event — for a caller that could not reach the endpoint; the reconciliation
/// handler resolves an effective provider id from the caller's token on a route no provider holds the scope
/// for. And neither adjustments nor the advice had a READ endpoint at all, for anyone: the only way to see
/// an adjustment was to have been the person who raised it.</para>
///
/// <para>Two things are asserted everywhere here. A provider reaches its OWN row and no other — and what it
/// receives is MASKED: the Mersal user who signed the adjustment or released the advice, and the internal
/// rationale behind it, are not the provider's business. The masking is proved by reading the same row as
/// staff in the same test, so "the field is absent" cannot be satisfied by a platform that lost the field.</para>
/// </summary>
[Collection("claims-db")]
public class ProviderPortalEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task A_provider_reads_the_adjustments_on_its_own_claim_masked_and_never_another_providers()
    {
        Skip.If(ClaimsApiFactory.Db is null, "test DB not configured — set CLAIMS_TEST_DB to run this DB integration test.");
        await using var app = new ClaimsApiFactory();
        try
        {
            using var officer = app.OfficerClient();
            var mine = Guid.NewGuid();
            var claimId = await SeedClaimAsync(app, officer, mine);
            await SeedAdjustmentAsync(app.Tenant, claimId);

            using var provider = app.ProviderAdminClient(mine);
            var read = await provider.GetAsync(new Uri($"/api/v1/claims/{claimId}/adjustments", UriKind.Relative));
            read.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await read.Content.ReadAsStringAsync());
            var json = await read.Content.ReadAsStringAsync();

            json.Should().Contain("PriceCorrection", "a provider is told WHAT was adjusted");
            json.Should().Contain(ReasonCodes.NoTariff, "…and the reason CODE, which is what an appeal argues with");
            json.Should().NotContain("adjustedBy", "the Mersal user who signed the adjustment is not the provider's business");
            json.Should().NotContain("priced against the wrong contract",
                "the internal rationale is staff commentary, not a statement to the counterparty");

            // The same row, read by staff: both fields are there. Without this the assertions above would
            // also pass on a projection that had quietly stopped carrying them for everyone.
            var staffRead = await officer.GetAsync(new Uri($"/api/v1/claims/{claimId}/adjustments", UriKind.Relative));
            staffRead.StatusCode.Should().Be(HttpStatusCode.OK);
            var staffJson = await staffRead.Content.ReadAsStringAsync();
            staffJson.Should().Contain("adjustedBy").And.Contain("priced against the wrong contract");

            using var otherProvider = app.ProviderAdminClient(Guid.NewGuid());
            (await otherProvider.GetAsync(new Uri($"/api/v1/claims/{claimId}/adjustments", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_provider_reads_its_own_settlement_advice_masked_and_never_another_providers()
    {
        Skip.If(ClaimsApiFactory.Db is null, "test DB not configured — set CLAIMS_TEST_DB to run this DB integration test.");
        await using var app = new ClaimsApiFactory();
        try
        {
            using var officer = app.OfficerClient();
            var mine = Guid.NewGuid();
            var batchId = await CreateBatchAsync(officer, mine);
            await SeedAdviceAsync(app.Tenant, batchId, mine);

            using var payee = app.ProviderAdminClient(mine);
            var read = await payee.GetAsync(new Uri($"/api/v1/claim-batches/{batchId}/settlement-advice", UriKind.Relative));
            read.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await read.Content.ReadAsStringAsync());
            var json = await read.Content.ReadAsStringAsync();

            json.Should().Contain("netPayable", "the net payable IS the advice — it is what the provider is owed");
            json.Should().Contain("contentHash", "the hash is how the provider proves the document it holds is the one issued");
            json.Should().NotContain("generatedBy", "which Mersal user released the payment is internal to Mersal");

            var staffJson = await (await officer.GetAsync(
                new Uri($"/api/v1/claim-batches/{batchId}/settlement-advice", UriKind.Relative))).Content.ReadAsStringAsync();
            staffJson.Should().Contain("generatedBy", "the releaser is on the staff projection — SoD is read there");

            using var otherProvider = app.ProviderAdminClient(Guid.NewGuid());
            (await otherProvider.GetAsync(new Uri($"/api/v1/claim-batches/{batchId}/settlement-advice", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// <c>E🔒🟠PO</c> — the provider downloads its own remittance advice. Export is a distinct, elevated,
    /// audited action (§3.3), so this rides on claims:export and not on the read scope; the cross-provider
    /// refusal below is the one SettlementService already wrote and audits as EXPORT_CROSS_PROVIDER.
    /// </summary>
    [SkippableFact]
    public async Task A_provider_exports_its_own_batch_and_is_refused_another_providers()
    {
        Skip.If(ClaimsApiFactory.Db is null, "test DB not configured — set CLAIMS_TEST_DB to run this DB integration test.");
        await using var app = new ClaimsApiFactory();
        try
        {
            using var officer = app.OfficerClient();
            var mine = Guid.NewGuid();
            var batchId = await CreateBatchAsync(officer, mine);

            using var payee = app.ProviderAdminClient(mine);
            var export = await payee.GetAsync(new Uri($"/api/v1/claim-batches/{batchId}/exports?format=CSV", UriKind.Relative));
            export.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await export.Content.ReadAsStringAsync());
            export.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");

            using var otherProvider = app.ProviderAdminClient(Guid.NewGuid());
            var refused = await otherProvider.GetAsync(new Uri($"/api/v1/claim-batches/{batchId}/exports?format=CSV", UriKind.Relative));
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await refused.Content.ReadAsStringAsync()).Should().Contain("provider-isolation");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// The line the export authority must NOT cross. Generating a settlement advice is the release step —
    /// the last human control before money moves (18.A4 / 36 §9) — and it is Mersal's act, not the payee's.
    /// A provider now holds claims:export, so the endpoint's scope check admits it and only the policy rule
    /// refuses: exactly the case where a coarse gate alone would have been enough to pay a provider on its
    /// own instruction.
    /// </summary>
    [SkippableFact]
    public async Task A_provider_cannot_generate_a_settlement_advice_even_holding_the_export_scope()
    {
        Skip.If(ClaimsApiFactory.Db is null, "test DB not configured — set CLAIMS_TEST_DB to run this DB integration test.");
        await using var app = new ClaimsApiFactory();
        try
        {
            using var officer = app.OfficerClient();
            var mine = Guid.NewGuid();
            var batchId = await CreateBatchAsync(officer, mine);

            using var payee = app.ProviderAdminClient(mine);
            var r = await payee.PostAsync(new Uri($"/api/v1/claim-batches/{batchId}/settlement-advice", UriKind.Relative), null);
            r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await r.Content.ReadAsStringAsync()).Should().Contain("claims-access-denied",
                "refused by the policy rule — not by the batch state, which would pass on the day the batch " +
                "happens to be Decided");

            await using var db = ClaimsApiFactory.Ctx();
            (await db.SettlementAdvices.CountAsync(a => a.BatchId == batchId)).Should().Be(0);
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    /// <summary>Rows are seeded through the DbContext rather than driven through the write path: reaching a
    /// settled advice over HTTP means deciding every line of a batch under dual control, which is another
    /// suite's subject. What is under test here is who may READ the row.</summary>
    private static async Task SeedAdjustmentAsync(string tenant, Guid claimId)
    {
        await using var db = ClaimsApiFactory.Ctx();
        var lineId = await db.ClaimLines.AsNoTracking().Where(l => l.ClaimId == claimId)
            .Select(l => l.ClaimLineId).FirstAsync();
        db.ClaimAdjustments.Add(new ClaimAdjustment
        {
            AdjustmentId = Guid.NewGuid(), ClaimId = claimId, ClaimLineId = lineId, TenantId = tenant,
            AdjustmentType = AdjustmentType.PriceCorrection, AmountDelta = -25m,
            ReasonCode = ReasonCodes.NoTariff, Rationale = "priced against the wrong contract",
            BeforeAmount = 200m, AfterAmount = 175m,
            AdjustedBy = ClaimsTestAuth.ReviewerSub, AdjustedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedAdviceAsync(string tenant, Guid batchId, Guid payeeProviderId)
    {
        await using var db = ClaimsApiFactory.Ctx();
        var batch = await db.ClaimBatches.AsNoTracking().SingleAsync(b => b.BatchId == batchId);
        db.SettlementAdvices.Add(new SettlementAdvice
        {
            AdviceId = Guid.NewGuid(), BatchId = batchId, TenantId = tenant, BatchNo = batch.BatchNo,
            PayeeProviderId = payeeProviderId, PeriodFrom = batch.PeriodFrom, PeriodTo = batch.PeriodTo,
            Version = 1, ContentHash = new string('a', 64), NetPayable = 175m, TotalClaimed = 200m,
            TotalPriced = 200m, TotalApproved = 175m, TotalAdjusted = -25m, TotalDenied = 0m,
            GeneratedBy = ClaimsTestAuth.ReviewerSub, GeneratedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SeedClaimAsync(ClaimsApiFactory app, HttpClient officer, Guid providerId)
    {
        var r = await officer.PostAsJsonAsync("/api/v1/claims/intake", new ClaimIntakeRequest(
            EventId: Guid.NewGuid(), EventType: "OrderLinesConsumed", TenantId: app.Tenant,
            FulfillmentRef: Guid.NewGuid(), FulfillmentType: FulfillmentType.OrderFulfillment,
            BeneficiaryId: Guid.NewGuid(), ProviderId: providerId, ProviderLocationId: null, AuthorizationId: null,
            CodeSystem: ClaimCodeSystem.CPT, Code: "80053", Description: "Metabolic panel",
            Quantity: 1, BilledAmount: 200m, ServiceDate: new DateOnly(2026, 7, 1), CurrencyCode: "EGP",
            OccurredAt: DateTimeOffset.UtcNow), Web);
        r.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("claimId").GetGuid();
    }

    private static async Task<Guid> CreateBatchAsync(HttpClient officer, Guid payeeProviderId)
    {
        var period = new DateOnly(2026, 7, 1);
        var r = await officer.PostAsJsonAsync("/api/v1/claim-batches", new CreateBatchRequest(
            BatchType.Provider, BatchSelectionMode.Manual, payeeProviderId, null, null,
            period, period.AddMonths(1), null, null, []), Web);
        r.StatusCode.Should().Be(HttpStatusCode.Created, "{0}", await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("batchId").GetGuid();
    }
}
