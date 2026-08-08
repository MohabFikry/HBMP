using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Orders.Api;
using Mersal.Orders.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Tests;

/// <summary>
/// 29.2 / design 45 §2 — an OP procedure IS an order, through the SAME machinery as a lab order.
///
/// <para>Design 45 §2's whole argument is that building a parallel mechanism "would fork the consume/authorise/
/// claim path that took several phases to get right". These tests are the evidence that it was not forked: a
/// Procedure order is created by the same endpoint, validated by the same code check, stamped with the same
/// validity, and metered by the same consume rule.</para>
/// </summary>
[Collection("orders-db")]
public class ProcedureOrderEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>97110 — therapeutic exercise. A Medicine code, which is where physiotherapy lives.</summary>
    private static CreateOrderRequest NewProcedure(
        string typeCode = "Physiotherapy", string cpt = "97110", decimal sessions = 6m) => new(
        BeneficiaryId: Guid.NewGuid(), EncounterId: Guid.NewGuid(), OrderType: OrderType.Procedure, ExpiresAt: null,
        Lines: [new CreateOrderLine(CodeSystem.CPT, cpt, "Therapeutic exercise", sessions, null, typeCode)]);

    [SkippableFact]
    public async Task A_surgery_or_medicine_code_creates_a_procedure_order_through_the_same_path_as_a_lab_order()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            using var doctor = app.DoctorClient();
            var r = await Post(doctor, NewProcedure());

            r.StatusCode.Should().Be(HttpStatusCode.Created);
            var order = await r.Content.ReadFromJsonAsync<JsonElement>();
            order.GetProperty("orderType").GetString().Should().Be("Procedure");

            // The SAME validity stamping every other order type gets — not a procedure-specific expiry path.
            order.GetProperty("expiresAt").ValueKind.Should().NotBe(JsonValueKind.Null,
                "a procedure order goes stale like any other clinical request");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Sessions_are_the_line_quantity_and_the_request_is_kept_beside_the_entitlement()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            using var doctor = app.DoctorClient();
            var r = await Post(doctor, NewProcedure(sessions: 10m));
            r.StatusCode.Should().Be(HttpStatusCode.Created);

            var lineId = (await r.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("lines")[0].GetProperty("orderLineId").GetGuid();

            await using var db = OrdersApiFactory.Ctx();
            var line = await db.OrderLines.AsNoTracking().FirstAsync(l => l.OrderLineId == lineId);

            // Ten sessions is quantity 10 — not a parallel counter (design 45 §2).
            line.QuantityOrdered.Should().Be(10m);
            line.RequestedQuantity.Should().Be(10m, "what was asked for is pinned at creation");
            line.ProcedureTypeCode.Should().Be("Physiotherapy");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// 31.1 — the COURSE at the ORDER level, and a line quantity that is PER SESSION.
    /// </summary>
    /// <remarks>
    /// This is the shape design 45 §2 could not express. Six attendances of three units each is one course
    /// and eighteen metered units; under "sessions ARE the quantity" there was nowhere to put the three, and
    /// a two-item course could carry two different session counts.
    /// </remarks>
    [SkippableFact]
    public async Task The_course_is_the_ORDERS_and_the_line_quantity_is_per_session()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            using var doctor = app.DoctorClient();
            var r = await Post(doctor, new CreateOrderRequest(
                BeneficiaryId: Guid.NewGuid(), EncounterId: Guid.NewGuid(),
                OrderType: OrderType.Procedure, ExpiresAt: null,
                Lines: [new CreateOrderLine(CodeSystem.CPT, "97110", "Therapeutic exercise",
                    QuantityOrdered: 0m, ExaminationTypeId: null, ProcedureTypeCode: null,
                    QuantityPerSession: 3m)],
                ProcedureTypeCode: "Physiotherapy",
                Sessions: 6));

            r.StatusCode.Should().Be(HttpStatusCode.Created);
            var orderId = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();

            await using var db = OrdersApiFactory.Ctx();
            var order = await db.Orders.AsNoTracking().Include(o => o.Lines)
                .FirstAsync(o => o.OrderId == orderId);

            order.ProcedureTypeCode.Should().Be("Physiotherapy", "a course is ONE clinical decision");
            order.Sessions.Should().Be(6);

            var line = order.Lines.Single();
            line.QuantityPerSession.Should().Be(3m);
            // The METERED total keeps its old meaning exactly — which is what leaves the atomic consume
            // path, the partial-approval arithmetic and the delivering centre's queue untouched.
            line.QuantityOrdered.Should().Be(18m);
            line.RequestedQuantity.Should().Be(18m, "what was asked for is pinned at creation");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_session_ceiling_is_checked_against_the_COURSE_not_the_metered_total()
    {
        // "At most 12 sessions" is a statement about ATTENDANCES. Comparing it to sessions x per-session
        // would refuse an ordinary 6-session course of a 3-per-visit item as though 18 had been asked for.
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            using var doctor = app.DoctorClient();
            var r = await Post(doctor, new CreateOrderRequest(
                BeneficiaryId: Guid.NewGuid(), EncounterId: Guid.NewGuid(),
                OrderType: OrderType.Procedure, ExpiresAt: null,
                Lines: [new CreateOrderLine(CodeSystem.CPT, "97110", "Therapeutic exercise",
                    QuantityOrdered: 0m, ExaminationTypeId: null, ProcedureTypeCode: null,
                    QuantityPerSession: 3m)],
                ProcedureTypeCode: "Physiotherapy",
                Sessions: 6));

            r.StatusCode.Should().Be(HttpStatusCode.Created);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_physiotherapy_type_on_a_minor_surgery_code_is_refused_on_the_WRITE_path()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            using var doctor = app.DoctorClient();
            // 29881 — knee arthroscopy, a Surgery code. Physiotherapy declares Medicine only.
            var r = await Post(doctor, NewProcedure(cpt: "29881"));

            r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            var problem = await r.Content.ReadFromJsonAsync<JsonElement>();
            problem.GetProperty("reason").GetString().Should().Be(nameof(ProcedureLineError.TypeSectionMismatch));
            problem.GetProperty("detail").GetString().Should().Contain("29881");
            problem.GetProperty("detailAr").GetString().Should().NotBeNullOrWhiteSpace("every refusal is bilingual");

            await using var db = OrdersApiFactory.Ctx();
            (await db.OrderLines.CountAsync(l => l.Code == "29881")).Should().Be(0, "nothing was written");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_procedure_order_without_a_type_is_refused_rather_than_defaulted()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            using var doctor = app.DoctorClient();
            var r = await Post(doctor, NewProcedure(typeCode: null!));

            r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString()
                .Should().Be(nameof(ProcedureLineError.TypeMissing), "defaulting to 'Other' would make the field decorative");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_retired_type_stops_being_orderable()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            using var doctor = app.DoctorClient();
            var r = await Post(doctor, NewProcedure(typeCode: "Retired"));

            r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString()
                .Should().Be(nameof(ProcedureLineError.TypeUnknown));
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Sessions_on_a_non_session_type_are_refused_not_silently_dropped()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            using var doctor = app.DoctorClient();
            // MinorSurgery is not session-based; a quantity of 5 is a session count wearing a quantity's
            // clothes. Dropping it silently would bill one delivery for what the doctor believed was five.
            var r = await Post(doctor, NewProcedure(typeCode: "MinorSurgery", cpt: "29881", sessions: 5m));

            r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString()
                .Should().Be(nameof(ProcedureLineError.SessionsNotSupported));
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_procedure_type_on_a_lab_order_is_refused()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            using var doctor = app.DoctorClient();
            var body = new CreateOrderRequest(
                Guid.NewGuid(), Guid.NewGuid(), OrderType.Lab, null,
                [new CreateOrderLine(CodeSystem.LOINC, "24331-1", "Lipid panel", 1m, null, "Physiotherapy")]);

            var r = await Post(doctor, body);

            r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString()
                .Should().Be(nameof(ProcedureLineError.TypeOnNonProcedureOrder),
                    "ignoring it would make every report grouped by procedure type quietly incomplete");
        }
        finally { await app.CleanupAsync(); }
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, CreateOrderRequest body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/investigation-orders")
        {
            Content = JsonContent.Create(body, options: Web),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await client.SendAsync(req);
    }
}
