using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Tests;

/// <summary>
/// What a terminal claim decision tells the rest of the platform about money.
///
/// <para><b>Why this file exists.</b> <c>reporting.financial_fact</c> — the table behind
/// <c>/reports/financial-summary</c> and the Medical Director's financial dashboard widget — was fed by a
/// projector case for <c>ServiceValued</c>, an event no service on this platform publishes. Reporting had a
/// projector test for it, so the read side was covered and green; nothing on the WRITE side asserted that
/// anybody sent the message. The result was a money report that returned zero in production for its entire
/// life while every test passed.</para>
///
/// <para>So the assertion here is deliberately about the PUBLISHER, and it runs over HTTP rather than against
/// <c>DecisionService</c>: the events are enqueued in the endpoint's <c>insideTransaction</c> callback, which
/// the service-level decision tests never execute. A test that called the service directly would prove the
/// decision and miss the publication, which is the exact half that was missing before.</para>
/// </summary>
[Collection("claims-db")]
public class SettlementCostFeedTests
{
    /// <summary>
    /// A settled claim publishes the claim-level cost AND one service-line fact per line.
    /// </summary>
    /// <remarks>
    /// Both, because they are different grains feeding different tables — <c>fact_cost</c> carries one row
    /// per claim with the payer and tier axes, <c>financial_fact</c> one row per service line — and a
    /// financial summary needs the breakdown that the claim-level event structurally cannot carry.
    /// </remarks>
    [SkippableFact]
    public async Task Settling_a_claim_publishes_the_claim_cost_and_a_fact_per_service_line()
    {
        Skip.If(ClaimsApiFactory.Db is null, "CLAIMS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ClaimsApiFactory();
        var (claimId, lines) = await SeedTwoLineClaimAsync(app.Tenant);

        using var officer = app.OfficerClient();
        foreach (var lineId in lines)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/claims/{claimId}/lines/{lineId}/decisions")
            {
                Content = JsonContent.Create(new { decision = "Approve", allowedAmount = 120m, rationale = "in tariff" }),
            };
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            (await officer.SendAsync(req)).EnsureSuccessStatusCode();
        }

        var published = app.Outbox.AllMessages.Select(m => m.EventType).ToList();

        // The claim-level terminal event: one, not one per line.
        published.Count(t => t == "ClaimApproved.v1").Should().Be(1,
            "the claim becomes terminal once, on the last line decided");

        // And the per-line service-line facts: one each.
        var lineEvents = app.Outbox.AllMessages.Where(m => m.EventType == "ClaimLineSettled.v1").ToList();
        lineEvents.Should().HaveCount(2,
            "the financial summary groups by service line, so the feed must carry one fact per line");

        // Every one of them is on the reporting feed — an event the relay does not mirror reaches no
        // projector, and this whole path exists to reach one.
        foreach (var e in lineEvents)
            ProjectionFeed.Includes(e.EventType).Should().BeTrue();
    }

    /// <summary>
    /// The line facts carry the service line, the code and the ALLOWED amount.
    /// </summary>
    /// <remarks>
    /// Allowed rather than billed, and it is the whole point of routing this through the settlement rather
    /// than through intake: a financial summary of what providers ASKED for is not a summary of what the
    /// benefit cost. The line below is billed at 200 and allowed at 120, so a payload carrying the wrong one
    /// is visible here rather than plausible.
    /// </remarks>
    [SkippableFact]
    public async Task A_service_line_fact_carries_the_allowed_amount_and_not_the_billed_one()
    {
        Skip.If(ClaimsApiFactory.Db is null, "CLAIMS_TEST_DB not set — DB integration test skipped.");
        await using var app = new ClaimsApiFactory();
        var (claimId, lines) = await SeedTwoLineClaimAsync(app.Tenant);

        using var officer = app.OfficerClient();
        foreach (var lineId in lines)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/claims/{claimId}/lines/{lineId}/decisions")
            {
                Content = JsonContent.Create(new { decision = "Approve", allowedAmount = 120m, rationale = "in tariff" }),
            };
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            (await officer.SendAsync(req)).EnsureSuccessStatusCode();
        }

        var payloads = app.Outbox.AllMessages
            .Where(m => m.EventType == "ClaimLineSettled.v1")
            .Select(m => m.Payload)
            .ToList();

        payloads.Should().NotBeEmpty();
        foreach (var p in payloads)
        {
            p.Should().Contain("\"amount\":120", "the settled cost is what was ALLOWED");
            p.Should().NotContain("\"amount\":200", "200 is what the provider billed, which is not a cost");
            p.Should().Contain("\"tenantId\"",
                "ProjectionMapping refuses a payload with no tenant rather than defaulting it, so a fact "
                + "without one is silently dropped at the projector");
            p.Should().Contain("\"serviceLine\"").And.Contain("\"serviceCode\"");
        }
    }

    /// <summary>Two lines on one claim under different coding systems, so a breakdown has something to
    /// break down. Seeded directly: intake has its own suite, and routing through it would make this test
    /// fail for reasons that are not about settlement.</summary>
    private static async Task<(Guid ClaimId, List<Guid> Lines)> SeedTwoLineClaimAsync(string tenant)
    {
        await using var db = ClaimsApiFactory.Ctx();
        var claim = new Claim
        {
            ClaimId = Guid.NewGuid(), ClaimNo = await new ClaimNoIssuer(db).NextAsync(2026),
            Origin = ClaimOrigin.AutoDerived, BeneficiaryId = Guid.NewGuid(), ProviderId = Guid.NewGuid(),
            TenantId = tenant, ServiceDateFrom = new DateOnly(2026, 7, 1), CurrencyCode = "EGP",
            ClaimedAmount = 400m, Status = ClaimStatus.UnderAdjudication, CreatedBy = "intake",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        claim.Lines.Add(new ClaimLine
        {
            ClaimLineId = Guid.NewGuid(), ClaimId = claim.ClaimId, CodeSystem = ClaimCodeSystem.CPT,
            Code = "80053", Quantity = 1, BilledAmount = 200m, ContractPrice = 180m,
            Status = ClaimLineStatus.Pending, FulfillmentRef = Guid.NewGuid(),
            FulfillmentType = FulfillmentType.OrderFulfillment,
        });
        claim.Lines.Add(new ClaimLine
        {
            // A DIFFERENT coding system from the line above, so the breakdown has two rows rather than one
            // — a per-line feed that collapsed to a single service line would pass a count assertion and
            // still answer no question.
            ClaimLineId = Guid.NewGuid(), ClaimId = claim.ClaimId, CodeSystem = ClaimCodeSystem.DRUG,
            Code = "71046", Quantity = 1, BilledAmount = 200m, ContractPrice = 180m,
            Status = ClaimLineStatus.Pending, FulfillmentRef = Guid.NewGuid(),
            FulfillmentType = FulfillmentType.DispenseEvent,
        });
        db.Claims.Add(claim);
        await db.SaveChangesAsync();
        return (claim.ClaimId, claim.Lines.Select(l => l.ClaimLineId).ToList());
    }
}
