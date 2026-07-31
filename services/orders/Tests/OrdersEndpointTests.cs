using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Orders.Api;
using Mersal.Orders.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Tests;

/// <summary>
/// Phase 24 Gate 3 — INV-CONSUME-ATOMIC and the gates around it, through the ENDPOINTS.
///
/// <para>OrderConsumeConcurrencyTests proves the executor: exactly one of two parallel callers wins, a
/// replayed key adds no row, a used line cannot be consumed again. All of it calls <c>ConsumeExecutor</c>
/// directly. What was never exercised is the endpoint that stands in front of it — the Idempotency-Key
/// requirement, the provider-ownership and Lab-vs-Imaging capability gate, and the mapping from each
/// executor outcome to its HTTP status. An over-consume answered 200 would have failed no test.</para>
/// </summary>
[Collection("orders-db")]
public class OrdersEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static CreateOrderRequest NewOrder(decimal qty = 2m) => new(
        BeneficiaryId: Guid.NewGuid(), EncounterId: Guid.NewGuid(), OrderType: OrderType.Lab, ExpiresAt: null,
        Lines: [new CreateOrderLine(CodeSystem.LOINC, "24331-1", "Lipid panel", qty)]);

    // ---- creation -----------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Creating_an_order_requires_an_idempotency_key_and_replays_under_the_same_one()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            using var doctor = app.DoctorClient();
            var body = NewOrder();

            var noKey = await doctor.PostAsJsonAsync("/api/v1/investigation-orders", body, Web);
            noKey.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await noKey.Content.ReadAsStringAsync()).Should().Contain("idempotency-required");

            var key = Guid.NewGuid().ToString();
            var created = await PostAsync(doctor, "/api/v1/investigation-orders", key, body);
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var orderId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();

            var replay = await PostAsync(doctor, "/api/v1/investigation-orders", key, body);
            replay.StatusCode.Should().Be(HttpStatusCode.OK);
            (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid()
                .Should().Be(orderId, "the same key is the same request, not a second order");

            await using var db = OrdersApiFactory.Ctx();
            (await db.Orders.CountAsync(o => o.TenantId == app.Tenant)).Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>Fail-closed on both sibling lookups: an unknown code and an absent treating relationship each
    /// refuse the order rather than letting it through on the benefit of the doubt.</summary>
    [SkippableFact]
    public async Task An_unknown_code_and_a_missing_treating_relationship_each_refuse_the_order()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory { CodesValid = false };
        try
        {
            using var doctor = app.DoctorClient();
            var unknown = await PostAsync(doctor, "/api/v1/investigation-orders", Guid.NewGuid().ToString(), NewOrder());
            unknown.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await unknown.Content.ReadAsStringAsync()).Should().Contain("unknown-code");
        }
        finally { await app.CleanupAsync(); }

        await using var noRelationship = new OrdersApiFactory { Treats = false };
        try
        {
            using var doctor = noRelationship.DoctorClient();
            var denied = await PostAsync(doctor, "/api/v1/investigation-orders", Guid.NewGuid().ToString(), NewOrder());
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "a doctor who is not treating this beneficiary may not order investigations for them");
        }
        finally { await noRelationship.CleanupAsync(); }
    }

    // ---- the consume gate ---------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_consume_without_an_idempotency_key_is_refused()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, lineId) = await SeedOrderAsync(app);
            using var lab = app.LabClient(Guid.NewGuid());

            var r = await lab.PostAsJsonAsync($"/api/v1/investigation-orders/{orderId}/consume",
                new { lines = new[] { new { orderLineId = lineId, quantity = 1m } } }, Web);
            r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await r.Content.ReadAsStringAsync()).Should().Contain("idempotency-required");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// INV-CONSUME-ATOMIC at the endpoint: the same key applies the effect exactly once. The executor proves
    /// this against itself; here it is proved through the HTTP surface a fulfilling provider actually calls,
    /// including that the replay is reported AS a replay rather than as a fresh success.
    /// </summary>
    [SkippableFact]
    public async Task Replaying_a_consume_key_applies_the_effect_once_and_says_so()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, lineId) = await SeedOrderAsync(app);
            using var lab = app.LabClient(Guid.NewGuid());
            var key = Guid.NewGuid().ToString();

            var first = await ConsumeAsync(lab, orderId, lineId, 1m, key);
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replayed").GetBoolean().Should().BeFalse();

            var again = await ConsumeAsync(lab, orderId, lineId, 1m, key);
            again.StatusCode.Should().Be(HttpStatusCode.OK);
            (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replayed").GetBoolean().Should().BeTrue();

            await using var db = OrdersApiFactory.Ctx();
            (await db.Fulfillments.CountAsync(f => f.OrderLineId == lineId)).Should().Be(1);
            var line = await db.OrderLines.AsNoTracking().SingleAsync(l => l.OrderLineId == lineId);
            line.QuantityRemaining.Should().Be(1m, "one of the two ordered units was consumed, exactly once");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>Asking for more than remains is refused 422 and moves nothing — the no-reuse half of
    /// INV-CONSUME-ATOMIC, at the status code a caller actually branches on.</summary>
    [SkippableFact]
    public async Task Consuming_more_than_remains_is_refused_and_changes_nothing()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, lineId) = await SeedOrderAsync(app);
            using var lab = app.LabClient(Guid.NewGuid());

            var over = await ConsumeAsync(lab, orderId, lineId, 5m, Guid.NewGuid().ToString());
            over.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await over.Content.ReadAsStringAsync()).Should().Contain("over-consume");

            await using var db = OrdersApiFactory.Ctx();
            (await db.Fulfillments.CountAsync(f => f.OrderLineId == lineId)).Should().Be(0);
            (await db.OrderLines.AsNoTracking().SingleAsync(l => l.OrderLineId == lineId))
                .QuantityRemaining.Should().Be(2m);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// The capability match, which is a domain rule enforced in the handler and nowhere else: an imaging
    /// technician holds the consume scope and the consume rule's role set, and still may not fulfil a Lab
    /// order. Scope alone is not capability.
    /// </summary>
    [SkippableFact]
    public async Task An_imaging_tech_may_not_fulfil_a_lab_order_even_holding_the_consume_scope()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, lineId) = await SeedOrderAsync(app);   // OrderType.Lab

            using var imaging = app.As(OrdersTestAuth.LabSub, "imaging_tech", "orders:consume orders:read");
            imaging.DefaultRequestHeaders.Add("X-Test-Provider", Guid.NewGuid().ToString());
            var r = await ConsumeAsync(imaging, orderId, lineId, 1m, Guid.NewGuid().ToString());
            r.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            await using var db = OrdersApiFactory.Ctx();
            (await db.Fulfillments.CountAsync(f => f.OrderLineId == lineId)).Should().Be(0);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>A caller with no provider affiliation cannot consume anything: there is no provider to
    /// attribute the fulfilment to, and provider-ownership is the ABAC condition on this action.</summary>
    [SkippableFact]
    public async Task A_technician_with_no_provider_affiliation_cannot_consume()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, lineId) = await SeedOrderAsync(app);
            using var unaffiliated = app.As(OrdersTestAuth.LabSub, "lab_tech", "orders:consume orders:read");

            var r = await ConsumeAsync(unaffiliated, orderId, lineId, 1m, Guid.NewGuid().ToString());
            r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>The doctor who ORDERED it cannot consume it. Ordering and fulfilling are different actions by
    /// different people, and the consume rule's role set is the separation.</summary>
    [SkippableFact]
    public async Task The_ordering_doctor_cannot_consume_their_own_order()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, lineId) = await SeedOrderAsync(app);
            using var doctor = app.As(OrdersTestAuth.DoctorSub, "doctor", "orders:write orders:read orders:consume");
            doctor.DefaultRequestHeaders.Add("X-Test-Provider", Guid.NewGuid().ToString());

            var r = await ConsumeAsync(doctor, orderId, lineId, 1m, Guid.NewGuid().ToString());
            r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Consuming_an_order_that_does_not_exist_is_a_404_before_any_gate_decision()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        using var lab = app.LabClient(Guid.NewGuid());
        var r = await ConsumeAsync(lab, Guid.NewGuid(), Guid.NewGuid(), 1m, Guid.NewGuid().ToString());
        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task An_unauthenticated_caller_is_refused_and_the_programme_gate_refuses_a_tenant_that_is_off()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        using var anonymous = app.CreateClient();
        (await anonymous.GetAsync(new Uri("/api/v1/investigation-orders/mine", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // On other programmes, just not this one.
        using var offProgramme = app.As(OrdersTestAuth.DoctorSub, "doctor", "orders:write orders:read",
            features: Mersal.Authz.ProgramFeatures.Emr);
        (await offProgramme.GetAsync(new Uri("/api/v1/investigation-orders/mine", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, string idempotencyKey, object body)
    {
        // Awaited inside the using: returning the task would dispose the content mid-send.
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Relative))
        {
            Content = JsonContent.Create(body, options: Web),
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(req);
    }

    private static Task<HttpResponseMessage> ConsumeAsync(
        HttpClient client, Guid orderId, Guid lineId, decimal quantity, string idempotencyKey) =>
        PostAsync(client, $"/api/v1/investigation-orders/{orderId}/consume", idempotencyKey,
            new { lines = new[] { new { orderLineId = lineId, quantity } } });

    private static async Task<(Guid OrderId, Guid LineId)> SeedOrderAsync(OrdersApiFactory app)
    {
        using var doctor = app.DoctorClient();
        var r = await PostAsync(doctor, "/api/v1/investigation-orders", Guid.NewGuid().ToString(), NewOrder());
        r.StatusCode.Should().Be(HttpStatusCode.Created, "the seed itself must succeed or every assertion below is vacuous");
        var order = await r.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = order.GetProperty("orderId").GetGuid();
        var lineId = order.GetProperty("lines")[0].GetProperty("orderLineId").GetGuid();
        return (orderId, lineId);
    }
}
