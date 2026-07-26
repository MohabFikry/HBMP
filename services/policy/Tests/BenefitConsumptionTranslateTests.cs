using System.Text.Json;
using FluentAssertions;
using Mersal.Policy.Api;
using Mersal.Policy.Domain;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 18.A1 — the event→instruction boundary, without a broker or a DB.
///
/// Two invariants are load-bearing here: (1) only FULFILLMENT events move the accumulator — the claims
/// path must never reach it (FR-CLM-057 / 36 §2.3); (2) a message we cannot attribute to a tenant
/// produces no instruction, so no write path ever guesses a tenant.
/// </summary>
public class BenefitConsumptionTranslateTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateOnly Today = new(2026, 7, 27);
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private static readonly Guid Beneficiary = new("bbbbbbbb-1111-4111-8111-111111111111");
    private static readonly Guid LineId = new("11111111-2222-4222-8222-222222222222");

    private static string ConsumePayload(string? tenant = Tenant, string? category = "LAB") =>
        JsonSerializer.Serialize(new
        {
            orderId = Guid.NewGuid(),
            tenantId = tenant,
            beneficiaryId = Beneficiary,
            benefitCategory = category,
            serviceDate = "2026-07-20",
            lines = new[] { new { orderLineId = LineId, quantity = 2m } },
            idempotencyKey = "idem-1",
        }, Web);

    [Fact]
    public void An_order_consume_event_becomes_one_instruction_per_line()
    {
        var result = BenefitConsumptionConsumer.Translate(Guid.NewGuid(), "OrderLinesConsumed", ConsumePayload(), Today);

        result.Should().ContainSingle();
        var i = result[0];
        i.TenantId.Should().Be(Tenant);
        i.BeneficiaryId.Should().Be(Beneficiary);
        i.BenefitCategory.Should().Be("LAB");
        i.Quantity.Should().Be(2m);
        i.Direction.Should().Be(ConsumptionDirection.Applied);
        i.OnDate.Should().Be(new DateOnly(2026, 7, 20));
    }

    [Fact]
    public void A_dispense_event_accumulates_against_PHARMACY()
    {
        var payload = JsonSerializer.Serialize(new
        {
            prescriptionId = Guid.NewGuid(), prescriptionLineId = LineId, tenantId = Tenant,
            beneficiaryId = Beneficiary, benefitCategory = "PHARMACY", serviceDate = "2026-07-20",
            quantity = 5m, batchNo = "B-1", idempotencyKey = "idem-2",
        }, Web);

        var result = BenefitConsumptionConsumer.Translate(Guid.NewGuid(), "RxLinesDispensed", payload, Today);

        result.Should().ContainSingle();
        result[0].BenefitCategory.Should().Be("PHARMACY");
        result[0].Quantity.Should().Be(5m);
    }

    [Fact]
    public void A_void_event_translates_to_a_symmetric_reversal()
    {
        var result = BenefitConsumptionConsumer.Translate(Guid.NewGuid(), "OrderFulfillmentVoided", ConsumePayload(), Today);

        result.Should().ContainSingle();
        result[0].Direction.Should().Be(ConsumptionDirection.Reversed);
    }

    [Fact]
    public void Claims_adjudication_never_moves_the_accumulator()
    {
        // The claims path reads limit_value − consumed_value and must never write it (FR-CLM-057).
        // Two guards: claims.events is not a fulfillment queue, and no claims event type translates.
        new ConsumptionConsumerOptions().FulfillmentQueues.Should().NotContain("claims.events");

        foreach (var claimEvent in new[] { "ClaimAdjudicated", "ClaimDecided", "ClaimBatchSettled", "ClaimAdjusted" })
            BenefitConsumptionConsumer.Translate(Guid.NewGuid(), claimEvent, ConsumePayload(), Today)
                .Should().BeEmpty("the claims path must never move coverage_limit.consumed_value");
    }

    [Fact]
    public void An_event_without_a_tenant_produces_no_instruction()
    {
        BenefitConsumptionConsumer.Translate(Guid.NewGuid(), "OrderLinesConsumed", ConsumePayload(tenant: null), Today)
            .Should().BeEmpty("a write path must never guess a tenant");
    }

    [Fact]
    public void A_procedure_order_carries_no_category_and_is_left_for_the_applier_to_record()
    {
        var result = BenefitConsumptionConsumer.Translate(Guid.NewGuid(), "OrderLinesConsumed", ConsumePayload(category: null), Today);

        result.Should().ContainSingle();
        result[0].BenefitCategory.Should().BeNull();
    }

    [Fact]
    public void Source_refs_separate_an_application_from_its_reversal()
    {
        var applied = BenefitAccumulation.SourceRef("OrderLinesConsumed", LineId, "k", ConsumptionDirection.Applied);
        var reversed = BenefitAccumulation.SourceRef("OrderLinesConsumed", LineId, "k", ConsumptionDirection.Reversed);

        applied.Should().NotBe(reversed);
    }

    [Theory]
    [InlineData(LimitType.Annual, true)]
    [InlineData(LimitType.Lifetime, true)]
    [InlineData(LimitType.Count, true)]
    [InlineData(LimitType.PerEncounter, false)]
    public void Only_cumulative_limit_kinds_accumulate(LimitType limitType, bool accumulates) =>
        BenefitAccumulation.Accumulates(limitType).Should().Be(accumulates);
}
