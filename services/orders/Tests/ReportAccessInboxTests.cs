using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Tests;

/// <summary>
/// 32.4 — the inbox a requester can see their own request in.
/// </summary>
/// <remarks>
/// <para>
/// 18.A4 added <c>supply-info</c> because "a request that entered InfoRequested had NO path back, so the
/// requester could never answer the question and the release was permanently stuck", and
/// <c>ReportAccessTests.A_request_in_InfoRequested_is_no_longer_stuck</c> has proven that transition legal
/// ever since. The product stayed stuck anyway, for a reason no state-machine test could see: <b>the
/// requester cannot reach the row</b>.
/// </para>
/// <para>
/// <c>GET /report-access-requests</c> returns what the caller may DECIDE — every pending request for a
/// medical director, and otherwise only requests against orders the caller placed. A clinician who asked to
/// see someone else's sensitive result is by definition not the ordering provider, so their own request has
/// never appeared in any list this platform serves. The screen's "Ask for more" button therefore walked a
/// requester into a state with no exit, and the exit had existed for months.
/// </para>
/// </remarks>
public class ReportAccessInboxTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Row(
        Guid RequestId, Guid OrderId, Guid OrderLineId, Guid BeneficiaryId, string RequestedBy,
        string? RequestedForRole, string PurposeCode, string Justification, int? RequestedTtlHours,
        string Status, DateTimeOffset CreatedAt, bool CanDecide, bool IsRequester);

    [SkippableFact]
    public async Task A_requester_sees_the_request_they_raised_even_though_they_decide_nothing()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            // The order belongs to somebody else: that is the ordinary case for a release request, and the
            // case in which the requester was invisible to themselves.
            var (orderId, lineId) = await SeedOrderAsync(app, orderingProvider: Guid.NewGuid());
            using var requester = app.As(OrdersTestAuth.DoctorSub, "doctor", "orders:read orders:write");
            var requestId = await RaiseAsync(requester, orderId, lineId);

            var rows = await ReadAsync(requester);

            var mine = rows.Should().ContainSingle(r => r.RequestId == requestId).Subject;
            mine.IsRequester.Should().BeTrue();
            mine.CanDecide.Should().BeFalse("the requester is not the author of the order they asked about");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_request_awaiting_the_requesters_answer_is_in_the_default_view()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, lineId) = await SeedOrderAsync(app, orderingProvider: Guid.NewGuid());
            using var requester = app.As(OrdersTestAuth.DoctorSub, "doctor", "orders:read orders:write");
            var requestId = await RaiseAsync(requester, orderId, lineId);
            await SetStatusAsync(requestId, ReportAccessStatus.InfoRequested);

            var rows = await ReadAsync(requester);

            // InfoRequested was in NEITHER default branch: not "needs a decision" (it needs an answer) and
            // not asked for by name. The one person who can move it could not find it.
            rows.Should().ContainSingle(r => r.RequestId == requestId && r.Status == "InfoRequested");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Someone_elses_request_on_someone_elses_order_stays_invisible()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, lineId) = await SeedOrderAsync(app, orderingProvider: Guid.NewGuid());
            using var other = app.As(Guid.NewGuid().ToString(), "doctor", "orders:read orders:write");
            var requestId = await RaiseAsync(other, orderId, lineId);

            using var me = app.As(OrdersTestAuth.DoctorSub, "doctor", "orders:read orders:write");
            var rows = await ReadAsync(me);

            // Widening the inbox to "mine" must not widen it to "everyone's". A release request names a
            // beneficiary and a clinician's stated reason for wanting their result.
            rows.Should().NotContain(r => r.RequestId == requestId);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_author_of_the_order_can_decide_and_is_told_so()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var author = Guid.Parse(OrdersTestAuth.DoctorSub);
            var (orderId, lineId) = await SeedOrderAsync(app, orderingProvider: author);
            using var other = app.As(Guid.NewGuid().ToString(), "doctor", "orders:read orders:write");
            var requestId = await RaiseAsync(other, orderId, lineId);

            using var me = app.As(OrdersTestAuth.DoctorSub, "doctor", "orders:read orders:write");
            var rows = await ReadAsync(me);

            var row = rows.Should().ContainSingle(r => r.RequestId == requestId).Subject;
            row.CanDecide.Should().BeTrue();
            row.IsRequester.Should().BeFalse();
        }
        finally { await app.CleanupAsync(); }
    }

    // ---------------------------------------------------------------- harness

    private static async Task<IReadOnlyList<Row>> ReadAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/api/v1/report-access-requests");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadFromJsonAsync<List<Row>>(Web) ?? [];
    }

    private static async Task<Guid> RaiseAsync(HttpClient client, Guid orderId, Guid lineId)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/report-access-requests", new
        {
            orderId,
            orderLineId = lineId,
            purposeCode = "ContinuityOfCare",
            justification = "Reviewing this patient's follow-up on 2026-08-20.",
            requestedTtlHours = 6,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        return body.GetProperty("requestId").GetGuid();
    }

    private static async Task SetStatusAsync(Guid requestId, ReportAccessStatus status)
    {
        await using var db = Ctx();
        var row = await db.ReportAccessRequests.SingleAsync(r => r.RequestId == requestId);
        row.Status = status;
        await db.SaveChangesAsync();
    }

    private static async Task<(Guid OrderId, Guid LineId)> SeedOrderAsync(OrdersApiFactory app, Guid orderingProvider)
    {
        await using var db = Ctx();
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        db.Orders.Add(new InvestigationOrder
        {
            OrderId = orderId,
            TenantId = app.Tenant,
            OrderNo = await new OrderNoIssuer(db).NextAsync(2026),
            BeneficiaryId = Guid.NewGuid(),
            EncounterId = Guid.NewGuid(),
            OrderType = OrderType.Lab,
            OrderingProviderId = orderingProvider,
            Status = OrderStatus.Active,
            RequestedAt = DateTimeOffset.UtcNow,
            Lines =
            [
                new OrderLine
                {
                    OrderLineId = lineId, TenantId = app.Tenant, CodeSystem = CodeSystem.CPT, Code = "80053",
                    SensitivityLevel = SensitivityLevel.Sensitive,
                    // ck_order_line_requested_positive: a line for none of something is not an order.
                    RequestedQuantity = 1m, QuantityOrdered = 1m,
                },
            ],
        });
        await db.SaveChangesAsync();
        return (orderId, lineId);
    }

    private static OrdersDbContext Ctx() =>
        new(new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(OrdersApiFactory.Db).UseSnakeCaseNamingConvention().Options);
}
