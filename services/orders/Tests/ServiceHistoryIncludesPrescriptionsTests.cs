using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Mersal.Orders.Tests;

/// <summary>
/// 29.4 (design 45 §4) — <b>prescription lines belong in the service history too.</b>
///
/// <para>Design 45 §4 opens by naming what the modal covers: "Every service line — prescription, lab,
/// radiology, OP procedure, and every history tab". The endpoint queried <c>db.Orders</c> and nothing else,
/// so it answered for three of the four. A prescriber asking "has this patient had this before?" about a
/// medicine got a confident empty answer.</para>
///
/// <para><b>ONE endpoint, still.</b> The alternative — a second endpoint in pharmacy that the client merges
/// — would put the sensitivity gate in two services, which is the drift design 45 §4 forbids in as many
/// words. orders-service composes under the CALLER'S token instead: pharmacy answers what that caller may
/// see, and pharmacy being unreachable is reported rather than rendered as "no prescriptions".</para>
/// </summary>
[Collection("orders-db")]
public class ServiceHistoryIncludesPrescriptionsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static List<JsonElement> Items(JsonElement e) => [.. e.GetProperty("items").EnumerateArray()];

    [SkippableFact]
    public async Task A_prescription_line_appears_in_the_history_beside_the_orders()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var beneficiary = Guid.NewGuid();
            app.Prescriptions.Add(new StubPrescriptionHistoryRow(
                PrescriptionId: Guid.NewGuid(), RxNo: "RX-2026-000001",
                PrescriptionLineId: Guid.NewGuid(), DrugId: Guid.NewGuid(),
                DrugName: "Metformin 500mg", OccurredAt: DateTimeOffset.UtcNow.AddMonths(-2),
                Status: "Dispensed", PrescriberId: "doc-1", BranchId: null));

            using var doctor = app.DoctorClient();
            var body = await (await doctor.GetAsync($"/api/v1/patients/{beneficiary}/service-history"))
                .Content.ReadFromJsonAsync<JsonElement>(Web);

            Items(body).Should().Contain(i => i.GetProperty("serviceType").GetString() == "Prescription");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Filtering_by_serviceType_Prescription_returns_only_prescriptions()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var beneficiary = Guid.NewGuid();
            app.Prescriptions.Add(new StubPrescriptionHistoryRow(
                PrescriptionId: Guid.NewGuid(), RxNo: "RX-2026-000002",
                PrescriptionLineId: Guid.NewGuid(), DrugId: Guid.NewGuid(),
                DrugName: "Amlodipine 5mg", OccurredAt: DateTimeOffset.UtcNow.AddMonths(-1),
                Status: "Dispensed", PrescriberId: "doc-1", BranchId: null));

            using var doctor = app.DoctorClient();
            var body = await (await doctor.GetAsync(
                    $"/api/v1/patients/{beneficiary}/service-history?serviceType=Prescription"))
                .Content.ReadFromJsonAsync<JsonElement>(Web);

            Items(body).Should().OnlyContain(i => i.GetProperty("serviceType").GetString() == "Prescription");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_history_for_a_LAB_code_does_not_go_asking_pharmacy()
    {
        // Narrowing matters: a service-history modal opened on a lab code has no business reading the
        // patient's medication list, and a PHI read nobody needed is still a PHI read.
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            using var doctor = app.DoctorClient();
            await doctor.GetAsync($"/api/v1/patients/{Guid.NewGuid()}/service-history?serviceType=Lab&code=80048");

            app.PrescriptionHistoryCalls.Should().Be(0,
                "a lab-scoped history must not reach into the medication record");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Pharmacy_being_unreachable_is_REPORTED_never_rendered_as_no_prescriptions()
    {
        // THE THREE-STATE RULE, at the seam. "Could not load" must never arrive as "none": a clinician
        // reading "no previous prescriptions" when pharmacy was simply down will re-prescribe.
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            app.PrescriptionHistoryFails = true;

            using var doctor = app.DoctorClient();
            var resp = await doctor.GetAsync(
                $"/api/v1/patients/{Guid.NewGuid()}/service-history?serviceType=Prescription");
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);

            resp.StatusCode.Should().Be(HttpStatusCode.OK, "the orders half still answered");
            body.GetProperty("prescriptionsUnavailable").GetBoolean().Should().BeTrue(
                "the caller must be able to say 'could not load' rather than 'none'");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_prescription_row_carries_no_result_value_because_a_prescription_has_none()
    {
        // The projection stays as narrow as the thing it describes. A dispensed medicine has a status, not
        // a result, and inventing an empty "result" column would imply one exists and was withheld.
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            app.Prescriptions.Add(new StubPrescriptionHistoryRow(
                PrescriptionId: Guid.NewGuid(), RxNo: "RX-2026-000003",
                PrescriptionLineId: Guid.NewGuid(), DrugId: Guid.NewGuid(),
                DrugName: "Metformin 500mg", OccurredAt: DateTimeOffset.UtcNow,
                Status: "Dispensed", PrescriberId: "doc-1", BranchId: null));

            using var doctor = app.DoctorClient();
            var body = await (await doctor.GetAsync(
                    $"/api/v1/patients/{Guid.NewGuid()}/service-history?serviceType=Prescription"))
                .Content.ReadFromJsonAsync<JsonElement>(Web);

            var row = Items(body).Single(i => i.GetProperty("serviceType").GetString() == "Prescription");
            row.GetProperty("resultSummary").ValueKind.Should().Be(JsonValueKind.Null);
            row.GetProperty("restricted").GetBoolean().Should().BeFalse();
        }
        finally { await app.CleanupAsync(); }
    }
}
