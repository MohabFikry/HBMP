using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Orders.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Tests;

/// <summary>
/// 29.4 / design 45 §4 — the per-line service-history endpoint.
///
/// <para><b>The test that matters is the sensitivity one.</b> This endpoint aggregates a patient's whole
/// history of one service onto one screen, which is exactly the shape that becomes a side door around the
/// design-37 §6 gate. "A history modal that reveals a mental-health result the results inbox withholds would
/// defeat the entire gate" — so the assertion is made over the SERIALIZED payload, because what the gate
/// protects is what crosses the wire, not what a DTO declares.</para>
/// </summary>
[Collection("orders-db")]
public class ServiceHistoryEndpointTests
{
    /// <summary>The branch every seeded order is raised at.
    ///
    /// <para>Set explicitly, and the caller sends the matching <c>X-Active-Branch</c>, because `doctor` is a
    /// BRANCH-SCOPED role: with no active branch <c>ApplyBranchScope</c> resolves to the no-branch sentinel
    /// and the query returns nothing. That is the gate working — a real order always pins the branch it was
    /// raised at — and a fixture that left it null would have been testing a permanently empty result.</para></summary>
    private static readonly Guid Branch = Guid.Parse("cccccccc-0000-4000-8000-00000000000c");

    /// <summary>A doctor assigned to <see cref="Branch"/>, which resolves as their HOME branch — so no
    /// X-Active-Branch header is needed and none is sent. Sending one the directory does not permit is a 403
    /// by design (an out-of-set active branch is refused and audited), which is a different test.</summary>
    private static HttpClient DoctorAtBranch(OrdersApiFactory app)
    {
        app.PermittedBranch = Branch;
        return app.DoctorClient();
    }

    [SkippableFact]
    public async Task A_restricted_result_is_existence_only_with_no_value_in_the_payload()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var beneficiary = Guid.NewGuid();
            await SeedAsync(app, beneficiary, code: "80048", sensitivity: SensitivityLevel.HighlySensitive,
                resultValue: "Anti-HCV reactive", createdBy: "another-clinician");

            using var doctor = DoctorAtBranch(app);
            var r = await doctor.GetAsync($"/api/v1/patients/{beneficiary}/service-history?code=80048");
            r.StatusCode.Should().Be(HttpStatusCode.OK);

            var raw = await r.Content.ReadAsStringAsync();

            // EXISTENCE metadata survives — the clinician learns the test happened, and when.
            raw.Should().Contain("80048");
            raw.Should().Contain("\"restricted\":true");

            // THE VALUE DOES NOT. Asserted on the raw bytes: a field that is present-but-null has still been
            // sent, and a client that receives it has received it.
            raw.Should().NotContain("Anti-HCV",
                "a history modal that reveals what the results inbox withholds defeats the whole gate");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_ordering_clinician_still_sees_their_own_restricted_result()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var beneficiary = Guid.NewGuid();
            // Authored BY the caller: SensitiveDisclosure exempts the ordering clinician, and a gate that
            // withheld a doctor's own result from them would be broken in the other direction — fail-closed
            // is not the same as fail-useless.
            await SeedAsync(app, beneficiary, "80048", SensitivityLevel.HighlySensitive,
                "Anti-HCV reactive", createdBy: OrdersTestAuth.DoctorSub);

            using var doctor = DoctorAtBranch(app);
            var raw = await (await doctor.GetAsync($"/api/v1/patients/{beneficiary}/service-history?code=80048"))
                .Content.ReadAsStringAsync();

            raw.Should().Contain("Anti-HCV");
            raw.Should().Contain("\"restricted\":false");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_standard_result_is_returned_in_full_with_its_trend()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var beneficiary = Guid.NewGuid();
            await SeedAsync(app, beneficiary, "85025", SensitivityLevel.Standard, "11.2", "another-clinician");
            await SeedAsync(app, beneficiary, "85025", SensitivityLevel.Standard, "12.8", "another-clinician");

            using var doctor = DoctorAtBranch(app);
            var body = await (await doctor.GetAsync($"/api/v1/patients/{beneficiary}/service-history?code=85025"))
                .Content.ReadFromJsonAsync<JsonElement>();

            body.GetProperty("total").GetInt32().Should().Be(2);
            // The TREND is the clinical point of the feature — "show the trend where results are numeric".
            body.GetProperty("trend").GetArrayLength().Should().Be(2);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_restricted_row_contributes_nothing_to_the_trend()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var beneficiary = Guid.NewGuid();
            await SeedAsync(app, beneficiary, "85025", SensitivityLevel.Standard, "11.2", "another-clinician");
            await SeedAsync(app, beneficiary, "85025", SensitivityLevel.HighlySensitive, "99.9", "another-clinician");

            using var doctor = DoctorAtBranch(app);
            var body = await (await doctor.GetAsync($"/api/v1/patients/{beneficiary}/service-history?code=85025"))
                .Content.ReadFromJsonAsync<JsonElement>();

            body.GetProperty("total").GetInt32().Should().Be(2, "the restricted occurrence still EXISTS");
            // A chart drawn across restricted points leaks their values through their position. The gate has
            // to bind on the aggregate as well as on the row.
            body.GetProperty("trend").GetArrayLength().Should().Be(1);
            (await (await doctor.GetAsync($"/api/v1/patients/{beneficiary}/service-history?code=85025"))
                .Content.ReadAsStringAsync()).Should().NotContain("99.9");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task No_previous_occurrences_is_an_empty_list_and_a_200_not_an_error()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            using var doctor = DoctorAtBranch(app);
            var r = await doctor.GetAsync($"/api/v1/patients/{Guid.NewGuid()}/service-history?code=85025");

            // THREE STATES, and this is the middle one. "No previous occurrences" is a real, successful
            // answer; only "could not load" is an error, and the client must be able to tell them apart —
            // a clinician reading "no previous tests" when the service was unreachable re-orders needlessly.
            r.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await r.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("total").GetInt32().Should().Be(0);
            body.GetProperty("items").GetArrayLength().Should().Be(0);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Every_open_is_an_audited_PHI_read_naming_the_patient_and_the_service()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var beneficiary = Guid.NewGuid();
            await SeedAsync(app, beneficiary, "85025", SensitivityLevel.Standard, "11.2", "another-clinician");

            using var doctor = DoctorAtBranch(app);
            await doctor.GetAsync($"/api/v1/patients/{beneficiary}/service-history?code=85025");

            var reads = app.AuditEvents.Where(e => e.EntityType == "service_history").ToList();

            reads.Should().NotBeEmpty("every open of the history modal is an audited PHI read");
            reads.Should().Contain(
                e => e.EntityId.Contains(beneficiary.ToString()) && e.EntityId.Contains("85025"),
                "the audit NAMES the patient and the service — 'someone read a history' is not an answer an "
                + "investigation can use");
            reads.Should().OnlyContain(e => e.FieldClasses.Contains("phi"));
        }
        finally { await app.CleanupAsync(); }
    }

    private static async Task SeedAsync(
        OrdersApiFactory app, Guid beneficiaryId, string code, SensitivityLevel sensitivity,
        string? resultValue, string createdBy)
    {
        await using var db = OrdersApiFactory.Ctx();
        var line = new OrderLine
        {
            OrderLineId = Guid.NewGuid(),
            TenantId = app.Tenant,
            CodeSystem = CodeSystem.CPT, Code = code, Description = "Seeded service",
            RequestedQuantity = 1, QuantityOrdered = 1, QuantityConsumed = 1,
            SensitivityLevel = sensitivity, Status = OrderLineStatus.Completed,
        };
        var order = new InvestigationOrder
        {
            OrderId = Guid.NewGuid(),
            TenantId = app.Tenant,
            OrderNo = $"ORD-2026-{Random.Shared.Next(100000, 999999)}",
            BeneficiaryId = beneficiaryId,
            EncounterId = Guid.NewGuid(),
            OrderingProviderId = Guid.NewGuid(),
            OrderingBranchId = Branch,
            OrderType = OrderType.Lab,
            Status = OrderStatus.Completed,
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(1, 300)),
            CreatedBy = createdBy,
            Lines = [line],
        };
        db.Orders.Add(order);
        // Saved BEFORE the fulfilment: order_fulfillment has a real FK to order_line, and EF has no
        // navigation between them to order the inserts by.
        await db.SaveChangesAsync();

        if (resultValue is not null)
        {
            db.Fulfillments.Add(new OrderFulfillment
            {
                FulfillmentId = Guid.NewGuid(),
                TenantId = app.Tenant,
                OrderLineId = line.OrderLineId,
                PerformingProviderId = Guid.NewGuid(),
                Quantity = 1,
                IdempotencyKey = $"seed-{Guid.NewGuid()}::{line.OrderLineId}",
                ResultValue = resultValue,
                ConsumedAt = order.RequestedAt.AddHours(2),
                ConsumedBy = Guid.NewGuid(),
            });
        }
        await db.SaveChangesAsync();
    }
}
