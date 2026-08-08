using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Mersal.Orders.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Tests;

/// <summary>
/// 30.6 — who may amend a signed order, and who may never (design 46 §7).
///
/// <para>The rules are enforced by REUSING the existing ordering gate rather than by a new check: the
/// amendment endpoints ask <c>OrdersPolicies.Create</c>, which already requires the <c>doctor</c> role, the
/// <c>orders:write</c> scope and a live treating relationship. That is deliberate — a second authorization
/// path is a second place for the treating rule to drift, and the drift would be invisible because both
/// paths would keep answering.</para>
///
/// <para>What is NOT inherited from that reuse is proof, so these are the denial tests the prompt asks for.
/// Each names the person being refused, because "403" alone does not say whether the rule is working or
/// whether the wrong rule is.</para>
/// </summary>
[Collection("orders-db")]
public class AmendmentAuthorityTests(OrdersApiFactory f) : IClassFixture<OrdersApiFactory>
{
    [SkippableFact]
    public async Task The_AUTHORING_PRESCRIBER_may_cancel_their_own_line()
    {
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var (orderId, lineId) = await Seed();
            (await CancelAs(f.DoctorClient(), orderId, lineId)).StatusCode
                .Should().Be(HttpStatusCode.OK, "the default authority is the doctor who wrote it");
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task ANOTHER_TREATING_CLINICIAN_may_amend_it_too()
    {
        // Design 46 §7: "cover happens, and a doctor who has gone home should not block a correction." The
        // coded reason is what makes that safe — the record says who changed it and why.
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var (orderId, lineId) = await Seed();
            var colleague = f.As("99999999-9999-9999-9999-999999999999", "doctor", "orders:write orders:read");
            colleague.DefaultRequestHeaders.Add("Idempotency-Key", $"cover-{Guid.NewGuid()}");

            var res = await colleague.PostAsJsonAsync(
                $"/api/v1/investigation-orders/{orderId}/lines/{lineId}/cancel",
                new { reasonCode = "ClinicalChange", reasonText = "covering for Dr Karim" });

            res.StatusCode.Should().Be(HttpStatusCode.OK);

            await using var db = OrdersApiFactory.Ctx();
            var line = await db.OrderLines.AsNoTracking().SingleAsync(l => l.OrderLineId == lineId);
            line.AmendedBy.Should().NotBe(Guid.Empty, "the record names WHO, not just that it changed");
            line.AmendmentReasonText.Should().Contain("covering");
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_NON_TREATING_clinician_is_refused()
    {
        // The ABAC condition, not a role check: the caller is a doctor with the right scope and no
        // relationship to this patient.
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var (orderId, lineId) = await Seed();
            f.Treats = false;
            try
            {
                (await CancelAs(f.DoctorClient(), orderId, lineId)).StatusCode
                    .Should().Be(HttpStatusCode.Forbidden);
            }
            finally { f.Treats = true; }
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableTheory]
    [InlineData("reception", "reception:search")]
    [InlineData("call_centre", "callcentre:read")]
    [InlineData("lab_tech", "orders:consume orders:read provider:read")]
    [InlineData("pharmacist", "pharmacy:read")]
    public async Task RECEPTION_the_CALL_CENTRE_and_the_FULFILLING_PROVIDER_are_refused(string role, string scopes)
    {
        // Design 46 §7: "Never reception, call centre, or the fulfilling provider. A pharmacy that disagrees
        // with a prescription raises a clarification; it does not edit it." The lab technician is the sharp
        // case — they hold orders scopes and touch this very order every day.
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var (orderId, lineId) = await Seed();
            var client = f.As(Guid.NewGuid().ToString(), role, scopes);

            (await CancelAs(client, orderId, lineId)).StatusCode.Should().BeOneOf(
                [HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized],
                "a fulfilling provider raises a clarification; it does not edit a clinician's order");

            await using var db = OrdersApiFactory.Ctx();
            (await db.OrderLines.AsNoTracking().SingleAsync(l => l.OrderLineId == lineId)).Status
                .Should().Be(OrderLineStatus.Active, "and the refusal changed nothing");
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_EXPIRED_order_is_not_amendable_it_is_expired()
    {
        // Design 46 §7: "Bounded by the order's own validity." Its own answer, not a generic refusal,
        // because the recovery is specific — the approval team can revalidate it.
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var (orderId, lineId) = await Seed(expired: true);
            var res = await CancelAs(f.DoctorClient(), orderId, lineId);

            res.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await res.Content.ReadAsStringAsync()).Should().Contain("urn:hbmp:order-expired",
                "an expired order is not amendable — it is expired, and the approval team can revalidate it");
        }
        finally { await f.CleanupAsync(); }
    }

    private static async Task<HttpResponseMessage> CancelAs(HttpClient client, Guid orderId, Guid lineId)
    {
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", $"auth-{Guid.NewGuid()}");
        return await client.PostAsJsonAsync(
            $"/api/v1/investigation-orders/{orderId}/lines/{lineId}/cancel",
            new { reasonCode = "ClinicalChange", reasonText = (string?)null });
    }

    private async Task<(Guid orderId, Guid lineId)> Seed(bool expired = false)
    {
        await using var db = OrdersApiFactory.Ctx();
        var line = new OrderLine
        {
            OrderLineId = Guid.NewGuid(), TenantId = f.Tenant, CodeSystem = CodeSystem.CPT,
            Code = "80053", QuantityOrdered = 1, RequestedQuantity = 1,
        };
        var order = new InvestigationOrder
        {
            OrderId = Guid.NewGuid(), TenantId = f.Tenant,
            OrderNo = await new Infrastructure.OrderNoIssuer(db).NextAsync(2026),
            BeneficiaryId = Guid.NewGuid(), EncounterId = Guid.NewGuid(), OrderingProviderId = Guid.NewGuid(),
            OrderType = OrderType.Lab, Status = OrderStatus.Active,
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-30),
            ExpiresAt = expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(30),
            CreatedBy = OrdersTestAuth.DoctorSub, Lines = [line],
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return (order.OrderId, line.OrderLineId);
    }
}
