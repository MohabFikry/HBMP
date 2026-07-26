using System.Net;
using FluentAssertions;
using Mersal.Orders.Api;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Tests;

/// <summary>Phase 14.6 — pinned examination-type sensitivity. The resolver is FAIL-CLOSED (unknown → null →
/// the endpoint 422s); the order's sensitivity is the strictest of its lines; and the denormalized column
/// round-trips at the datastore (env-gated <c>ORDERS_TEST_DB</c>). Self-cleans by a unique beneficiary scope.</summary>
public class SensitivityTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("ORDERS_TEST_DB");
    private static OrdersDbContext Ctx() =>
        new(new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private sealed class StubHandler(HttpStatusCode code, string? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = body is null ? null : new StringContent(body) });
    }

    [Fact]
    public async Task Resolver_is_fail_closed_on_an_unknown_examination_type()
    {
        var http = new HttpClient(new StubHandler(HttpStatusCode.NotFound, null)) { BaseAddress = new Uri("http://md") };
        var resolver = new HttpExaminationTypeResolver(http);
        (await resolver.ResolveAsync(Guid.NewGuid(), bearer: null)).Should().BeNull("unknown examination types must fail closed");
    }

    [Fact]
    public async Task Resolver_pins_the_sensitivity_from_master_data()
    {
        const string body = """{"examinationTypeId":"0190c100-0000-7000-8000-000000000010","sensitivityLevel":"Sensitive","sensitiveCategory":"MentalHealth"}""";
        var http = new HttpClient(new StubHandler(HttpStatusCode.OK, body)) { BaseAddress = new Uri("http://md") };
        var cls = await new HttpExaminationTypeResolver(http).ResolveAsync(Guid.NewGuid(), bearer: null);
        cls!.SensitivityLevel.Should().Be(SensitivityLevel.Sensitive);
        cls.SensitiveCategory.Should().Be("MentalHealth");
    }

    [Fact]
    public void Order_sensitivity_is_the_strictest_of_its_lines()
    {
        SensitivityLevel[] lines = [SensitivityLevel.Standard, SensitivityLevel.Sensitive, SensitivityLevel.Standard];
        lines.Max().Should().Be(SensitivityLevel.Sensitive);
        new[] { SensitivityLevel.Sensitive, SensitivityLevel.HighlySensitive }.Max().Should().Be(SensitivityLevel.HighlySensitive);
    }

    [SkippableFact]
    public async Task Pinned_sensitivity_round_trips_at_the_datastore()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var order = new InvestigationOrder
            {
                OrderId = Guid.NewGuid(), OrderNo = "ORD-SENS-" + Guid.NewGuid().ToString("N")[..8],
                BeneficiaryId = beneficiary, EncounterId = Guid.NewGuid(), OrderingProviderId = Guid.NewGuid(),
                OrderType = OrderType.Lab, Status = OrderStatus.Active, RequestedAt = now,
                SensitivityLevel = SensitivityLevel.Sensitive,
                Lines = [new OrderLine
                {
                    OrderLineId = Guid.NewGuid(), CodeSystem = CodeSystem.CPT, Code = "90791", QuantityOrdered = 1,
                    Status = OrderLineStatus.Active, ExaminationTypeId = Guid.NewGuid(), SensitivityLevel = SensitivityLevel.Sensitive,
                }],
            };
            await using (var db = Ctx()) { db.Orders.Add(order); await db.SaveChangesAsync(); }
            await using var verify = Ctx();
            var back = await verify.Orders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.OrderId == order.OrderId);
            back.SensitivityLevel.Should().Be(SensitivityLevel.Sensitive);
            back.Lines[0].SensitivityLevel.Should().Be(SensitivityLevel.Sensitive);
        }
        finally
        {
            await using var db = Ctx();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM orders.order_line WHERE order_id IN (SELECT order_id FROM orders.investigation_order WHERE beneficiary_id = {0}); DELETE FROM orders.investigation_order WHERE beneficiary_id = {0};", beneficiary);
        }
    }
}
