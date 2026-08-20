using FluentAssertions;
using Mersal.Finance.Domain;
using Mersal.Finance.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Mersal.Time;

namespace Mersal.Finance.Tests;

/// <summary>Finance-service at the datastore (env-gated <c>FINANCE_TEST_DB</c>; needs the hbmp superuser conn).
/// Proves: (1) the projector builds utilization_fact from a delivery event carrying ONLY billing fields and IGNORES
/// a clinical key that sneaks onto the event (finance ≠ diagnosis at the projection boundary); (2) a settlement is
/// priced from the (read-not-owned) contract price book × delivered quantity with correct totals; and (3)
/// utilization aggregation sums authorized-vs-delivered + spend. Serialized via the finance-db collection. No-ops
/// without the env var.</summary>
[Collection("finance-db")]
public class FinanceIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("FINANCE_TEST_DB");

    private static DbContextOptions<FinanceDbContext> Options() =>
        new DbContextOptionsBuilder<FinanceDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;

    private static FinanceEvent Ev(string type, string tenant, DateTimeOffset at, params (string, string)[] fields) =>
        new(Guid.NewGuid(), type, tenant, fields.ToDictionary(f => f.Item1, f => f.Item2), at);

    [SkippableFact]
    public async Task Projector_builds_utilization_from_billing_fields_and_ignores_clinical_keys()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var day = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = new FinanceDbContext(Options());
            var proj = new FinanceEventProjector(db, TimeProvider.System, new BusinessCalendar(TimeProvider.System));
            // A delivery event that ALSO (wrongly) carries a diagnosis key — the projector must not persist it.
            await proj.ProjectAsync(Ev("OrderLineConsumed", tenant, day,
                ("serviceCode", "80053"), ("serviceLine", "Lab"), ("authorizedQty", "2"), ("deliveredQty", "2"),
                ("unitCost", "125.00"), ("diagnosis", "E11.9"), ("icd", "E11.9")));

            var fact = await db.UtilizationFacts.AsNoTracking().SingleAsync(f => f.TenantId == tenant);
            fact.ServiceCode.Should().Be("80053");
            fact.DeliveredQty.Should().Be(2);
            fact.LineCost.Should().Be(250.00m);
            // Structural: there is no column that could have stored the diagnosis key.
            typeof(UtilizationFact).GetProperties().Select(p => p.Name.ToLowerInvariant())
                .Should().NotContain(p => p.Contains("diagnosis") || p.Contains("icd"));
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Settlement_is_priced_from_the_contract_price_book_with_correct_totals()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var provider = Guid.NewGuid();
        var day = new DateTimeOffset(2026, 7, 5, 9, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = new FinanceDbContext(Options());
            var proj = new FinanceEventProjector(db, TimeProvider.System, new BusinessCalendar(TimeProvider.System));
            await proj.ProjectAsync(Ev("OrderLineConsumed", tenant, day,
                ("serviceCode", "70450"), ("serviceLine", "Radiology"), ("deliveredQty", "2"),
                ("providerId", provider.ToString()), ("unitCost", "300")));
            await proj.ProjectAsync(Ev("OrderLineConsumed", tenant, day,
                ("serviceCode", "70450"), ("serviceLine", "Radiology"), ("deliveredQty", "1"),
                ("providerId", provider.ToString()), ("unitCost", "300")));

            var prices = new FakePrices(new Dictionary<string, decimal> { ["70450"] = 350.00m });
            var gen = new SettlementGenerator(db, prices, new SettlementNoIssuer(db), TimeProvider.System);
            var s = await gen.GenerateAsync(tenant, provider, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), "fin-1", null);

            s.Lines.Should().ContainSingle();
            var line = s.Lines[0];
            line.ServiceCode.Should().Be("70450");
            line.DeliveredQty.Should().Be(3);              // 2 + 1
            line.AgreedUnitPrice.Should().Be(350.00m);     // from the contract book, not the 300 observed cost
            line.LineTotal.Should().Be(1050.00m);
            line.PriceSource.Should().Be(SettlementPriceSource.Contract);
            s.Total.Should().Be(1050.00m);
        }
        finally { await Cleanup(tenant); }
    }

    /// <summary>
    /// 2026-08-09 audit §2.3 — a code the contract does not price settles at the observed FLOOR, and says so.
    ///
    /// <para>It used to settle at the observed AVERAGE. The three deliveries below are the shape that makes
    /// that wrong: two at the ordinary 100, and one small one mispriced at 400. The average is 200 — double
    /// the real rate — and it is applied to every unit in the period, so one bad row at 400 turns a 500
    /// settlement into a 1,000 one with nothing on the line saying a tariff was never involved.</para>
    ///
    /// <para>The floor can only under-state, which is the direction chosen deliberately: a provider queries
    /// an underpayment and nobody queries an overpayment. The settlement is a Draft either way — what the
    /// reviewer needs is to be able to SEE which lines have no tariff behind them, which is what
    /// <c>PriceSource</c> is for.</para>
    /// </summary>
    [SkippableFact]
    public async Task An_unpriced_code_settles_at_the_observed_floor_and_is_marked_as_such()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var provider = Guid.NewGuid();
        var day = new DateTimeOffset(2026, 7, 10, 10, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = new FinanceDbContext(Options());
            var proj = new FinanceEventProjector(db, TimeProvider.System, new BusinessCalendar(TimeProvider.System));
            foreach (var unit in new[] { "100", "100", "400" })
                await proj.ProjectAsync(Ev("OrderLineConsumed", tenant, day,
                    ("serviceCode", "99999"), ("serviceLine", "Other"), ("deliveredQty", "1"),
                    ("providerId", provider.ToString()), ("unitCost", unit)));

            // A price book that knows every code EXCEPT this one.
            var prices = new FakePrices(new Dictionary<string, decimal> { ["70450"] = 350.00m });
            var gen = new SettlementGenerator(db, prices, new SettlementNoIssuer(db), TimeProvider.System);
            var s = await gen.GenerateAsync(tenant, provider, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), "fin-1", null);

            var line = s.Lines.Should().ContainSingle().Subject;
            line.AgreedUnitPrice.Should().Be(100.00m, "the floor — the average would be 200 and the 400 is the outlier");
            line.DeliveredQty.Should().Be(3);
            line.LineTotal.Should().Be(300.00m);
            line.PriceSource.Should().Be(SettlementPriceSource.ObservedFloor);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Utilization_aggregates_authorized_vs_delivered_and_spend()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var day = new DateTimeOffset(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = new FinanceDbContext(Options());
            var proj = new FinanceEventProjector(db, TimeProvider.System, new BusinessCalendar(TimeProvider.System));
            await proj.ProjectAsync(Ev("OrderLineConsumed", tenant, day,
                ("serviceCode", "80053"), ("authorizedQty", "5"), ("deliveredQty", "4"), ("lineCost", "200")));

            var q = new FinanceQueries(db);
            var view = await q.UtilizationAsync(tenant, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), null, null, null);
            view.TotalAuthorized.Should().Be(5);
            view.TotalDelivered.Should().Be(4);
            view.TotalSpend.Should().Be(200m);
        }
        finally { await Cleanup(tenant); }
    }

    private static async Task Cleanup(string tenant)
    {
        await using var db = new FinanceDbContext(Options());
        await db.Database.ExecuteSqlRawAsync("DELETE FROM finance.settlement_line WHERE settlement_id IN (SELECT settlement_id FROM finance.settlement WHERE tenant_id = {0});", tenant);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM finance.settlement WHERE tenant_id = {0};", tenant);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM finance.utilization_fact WHERE tenant_id = {0};", tenant);
    }

    private sealed class FakePrices(Dictionary<string, decimal> book) : IContractPriceProvider
    {
        public Task<ContractPriceBook?> GetPriceBookAsync(Guid providerId, DateOnly asOf, string? bearerToken, CancellationToken ct = default) =>
            Task.FromResult<ContractPriceBook?>(new ContractPriceBook(Guid.NewGuid(), "EGP", book));
    }
}
