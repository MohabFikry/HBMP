using FluentAssertions;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Tests;

/// <summary>claims-service auto-derive at the datastore (env-gated <c>CLAIMS_TEST_DB</c>; needs the hbmp superuser
/// conn). Proves: (1) an intake event creates exactly ONE priced claim_line anchored to the fulfillment ref; (2) the
/// SAME event redelivered is a no-op (one line, one processed_event) — idempotent; (3) a missing tariff records
/// NO_TARIFF + RequiresManualReview with a null price (never guessed). Serialized via the claims-db collection;
/// no-ops without the env var. Self-cleans by tenant scope.</summary>
[Collection("claims-db")]
public class ClaimsIntakeIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("CLAIMS_TEST_DB");
    private static DbContextOptions<ClaimsDbContext> Options() =>
        new DbContextOptionsBuilder<ClaimsDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;
    private static ClaimsDbContext Ctx() => new(Options());

    private static ClaimIntakeEvent Ev(string tenant, Guid fulfillmentRef, decimal billed = 200m) => new(
        EventId: Guid.NewGuid(), EventType: "OrderLinesConsumed", TenantId: tenant,
        FulfillmentRef: fulfillmentRef, FulfillmentType: FulfillmentType.OrderFulfillment,
        BeneficiaryId: Guid.NewGuid(), ProviderId: Guid.NewGuid(), ProviderLocationId: null, AuthorizationId: null,
        CodeSystem: ClaimCodeSystem.CPT, Code: "80053", Description: "Metabolic panel",
        Quantity: 1, BilledAmount: billed, ServiceDate: new DateOnly(2026, 7, 1), CurrencyCode: "EGP",
        OccurredAt: DateTimeOffset.UtcNow);

    private static ClaimIntakeExecutor Exec(ClaimsDbContext db, decimal? tariff) =>
        new(db, new ClaimNoIssuer(db), new FixedTariff(tariff), TimeProvider.System);

    [Fact]
    public async Task Intake_creates_one_priced_line_anchored_to_the_fulfillment_ref()
    {
        if (Db is null) return;
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var fref = Guid.NewGuid();
        try
        {
            await using var db = Ctx();
            var r = await Exec(db, tariff: 150m).IngestAsync(Ev(tenant, fref), null);
            r.Outcome.Should().Be(IntakeOutcome.Created);

            await using var verify = Ctx();
            var line = await verify.ClaimLines.AsNoTracking().SingleAsync(l => l.FulfillmentRef == fref);
            line.ContractPrice.Should().Be(150m);
            line.Status.Should().Be(ClaimLineStatus.Pending);
            line.ReasonCodes.Should().BeEmpty();
            var claim = await verify.Claims.AsNoTracking().SingleAsync(c => c.ClaimId == line.ClaimId);
            claim.TenantId.Should().Be(tenant);
            claim.ClaimNo.Should().MatchRegex(@"^CLM-\d{4}-\d{6}$");
        }
        finally { await Cleanup(tenant); }
    }

    [Fact]
    public async Task Same_event_redelivered_creates_no_second_line()
    {
        if (Db is null) return;
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var fref = Guid.NewGuid();
        try
        {
            var ev = Ev(tenant, fref);
            await using (var db1 = Ctx()) (await Exec(db1, 150m).IngestAsync(ev, null)).Outcome.Should().Be(IntakeOutcome.Created);
            await using (var db2 = Ctx()) (await Exec(db2, 150m).IngestAsync(ev, null)).Outcome.Should().Be(IntakeOutcome.Replayed);

            await using var verify = Ctx();
            (await verify.ClaimLines.CountAsync(l => l.FulfillmentRef == fref)).Should().Be(1);
            (await verify.ProcessedEvents.CountAsync(p => p.EventId == ev.EventId)).Should().Be(1);
        }
        finally { await Cleanup(tenant); }
    }

    [Fact]
    public async Task No_tariff_records_manual_review_and_a_null_price()
    {
        if (Db is null) return;
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var fref = Guid.NewGuid();
        try
        {
            await using var db = Ctx();
            await Exec(db, tariff: null).IngestAsync(Ev(tenant, fref), null);

            await using var verify = Ctx();
            var line = await verify.ClaimLines.AsNoTracking().SingleAsync(l => l.FulfillmentRef == fref);
            line.ContractPrice.Should().BeNull("no tariff must never be defaulted or guessed");
            line.SystemRecommendation.Should().Be(SystemRecommendation.RequiresManualReview);
            line.ReasonCodes.Should().Contain(ReasonCodes.NoTariff);
        }
        finally { await Cleanup(tenant); }
    }

    private static async Task Cleanup(string tenant)
    {
        if (Db is null) return;
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM claims.claim_line WHERE claim_id IN (SELECT claim_id FROM claims.claim WHERE tenant_id = {0}); " +
            "DELETE FROM claims.claim WHERE tenant_id = {0};", tenant);
    }
}
