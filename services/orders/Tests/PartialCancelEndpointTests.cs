using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Orders.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Tests;

/// <summary>
/// 30.1/30.2 — the acceptance criterion from design 46 §10, through the endpoint:
/// <b>"a 3-line prescription with line 1 dispensed allows lines 2–3 to be cancelled and reports partial
/// success plainly."</b>
///
/// <para>Asserted at the HTTP edge rather than on the plan, because the thing that goes wrong is the
/// REPORTING. The plan can be right and the response still tell a doctor that an order was withdrawn when
/// a third of it is live — a 200 with an empty list reads as "done" on a screen.</para>
/// </summary>
[Collection("orders-db")]
public class PartialCancelEndpointTests(OrdersApiFactory f) : IClassFixture<OrdersApiFactory>
{
    [SkippableFact]
    public async Task One_consumed_line_of_three_yields_207_naming_which_lines_and_why()
    {
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var (orderId, consumed, _, _) = await SeedThreeLineOrderWithOneConsumed();

            var doctor = f.As(OrdersTestAuth.DoctorSub, "doctor", "orders:write orders:read");
            doctor.DefaultRequestHeaders.Add("Idempotency-Key", $"bulk-{Guid.NewGuid()}");
            var res = await doctor.PostAsJsonAsync($"/api/v1/investigation-orders/{orderId}/cancel-lines",
                new { reasonCode = "ClinicalChange", reasonText = "patient improved" });

            res.StatusCode.Should().Be((HttpStatusCode)207,
                "a partial withdrawal is neither a success nor a failure, and reporting it as either "
                + "misinforms the doctor about what is still live");

            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("cancelled").GetInt32().Should().Be(2);

            var lines = body.GetProperty("lines").EnumerateArray().ToList();
            lines.Should().HaveCount(3);

            var refused = lines.Single(l => l.GetProperty("orderLineId").GetGuid() == consumed);
            refused.GetProperty("cancelled").GetBoolean().Should().BeFalse();
            refused.GetProperty("refusal").GetString().Should().NotBeNullOrWhiteSpace(
                "'some lines could not be cancelled' is not something a doctor can act on");

            await using var db = OrdersApiFactory.Ctx();
            var after = await db.OrderLines.AsNoTracking().Where(l => l.OrderId == orderId).ToListAsync();
            after.Count(l => l.Status == OrderLineStatus.Cancelled).Should().Be(2);
            after.Single(l => l.OrderLineId == consumed).Status
                .Should().Be(OrderLineStatus.Completed, "the delivered line is fact and is untouched");
            after.Where(l => l.Status == OrderLineStatus.Cancelled)
                .Should().OnlyContain(l => l.AmendmentReasonCode == "ClinicalChange" && l.AmendedAt != null);
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_order_with_nothing_cancellable_is_a_409_not_a_200_with_an_empty_list()
    {
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var (orderId, _, _, _) = await SeedThreeLineOrderWithOneConsumed(cancelTheRest: true);

            var doctor = f.As(OrdersTestAuth.DoctorSub, "doctor", "orders:write orders:read");
            doctor.DefaultRequestHeaders.Add("Idempotency-Key", $"bulk-{Guid.NewGuid()}");
            var res = await doctor.PostAsJsonAsync($"/api/v1/investigation-orders/{orderId}/cancel-lines",
                new { reasonCode = "Duplicate", reasonText = (string?)null });

            res.StatusCode.Should().Be(HttpStatusCode.Conflict,
                "a 200 with an empty cancelled-list reads as 'done', and the doctor walks away believing an "
                + "order was withdrawn that is still live");
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_missing_idempotency_key_is_refused()
    {
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var (orderId, _, _, _) = await SeedThreeLineOrderWithOneConsumed();
            var doctor = f.As(OrdersTestAuth.DoctorSub, "doctor", "orders:write orders:read");
            var res = await doctor.PostAsJsonAsync($"/api/v1/investigation-orders/{orderId}/cancel-lines",
                new { reasonCode = "Duplicate", reasonText = (string?)null });
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_reason_vocabulary_is_served_for_the_picker()
    {
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var doctor = f.As(OrdersTestAuth.DoctorSub, "doctor", "orders:read");
        var res = await doctor.GetAsync("/api/v1/investigation-orders/amendment-reasons");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        // Read the body ONCE — the content stream is not rewindable, and a second read yields nothing.
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var rows = body.EnumerateArray().ToList();
        var codes = rows.Select(r => r.GetProperty("code").GetString()).ToList();
        codes.Should().Contain(["PrescribingError", "ClinicalChange", "Duplicate", "Other"]);
        codes.Should().NotContain("DoseCorrection",
            "a drug-specific reason must not be offered on a lab order — a vocabulary that offers nonsense "
            + "gets used for nonsense");

        var arabic = rows.Select(r => r.GetProperty("nameAr").GetString());
        arabic.Should().OnlyContain(n => !string.IsNullOrWhiteSpace(n), "the picker is bilingual");
    }

    private async Task<(Guid orderId, Guid consumed, Guid b, Guid c)> SeedThreeLineOrderWithOneConsumed(
        bool cancelTheRest = false)
    {
        await using var db = OrdersApiFactory.Ctx();
        var lines = Enumerable.Range(0, 3).Select(i => new OrderLine
        {
            OrderLineId = Guid.NewGuid(), TenantId = f.Tenant, CodeSystem = CodeSystem.CPT,
            Code = $"8005{i}", QuantityOrdered = 1, RequestedQuantity = 1,
            // Line 0 is already delivered — the fact the other two are cancelled around.
            QuantityConsumed = i == 0 ? 1 : 0,
            Status = i == 0 ? OrderLineStatus.Completed
                : cancelTheRest ? OrderLineStatus.Cancelled : OrderLineStatus.Active,
            AmendmentReasonCode = i != 0 && cancelTheRest ? "Duplicate" : null,
            AmendedBy = i != 0 && cancelTheRest ? Guid.NewGuid() : null,
            AmendedAt = i != 0 && cancelTheRest ? DateTimeOffset.UtcNow : null,
        }).ToList();

        var order = new InvestigationOrder
        {
            OrderId = Guid.NewGuid(), TenantId = f.Tenant,
            OrderNo = await new Infrastructure.OrderNoIssuer(db).NextAsync(2026),
            BeneficiaryId = Guid.NewGuid(), EncounterId = Guid.NewGuid(), OrderingProviderId = Guid.NewGuid(),
            OrderType = OrderType.Lab, Status = OrderStatus.PartiallyUsed, RequestedAt = DateTimeOffset.UtcNow,
            Lines = lines,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return (order.OrderId, lines[0].OrderLineId, lines[1].OrderLineId, lines[2].OrderLineId);
    }
}
