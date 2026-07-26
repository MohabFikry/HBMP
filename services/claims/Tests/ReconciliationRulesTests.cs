using FluentAssertions;
using Mersal.Claims.Domain;

namespace Mersal.Claims.Tests;

/// <summary>Pure-domain unit tests for reconciliation + adjustments (10b.7, 36 §7): bucket classification for each of
/// the six buckets and the documented precedence when a line qualifies for two, plus adjustment sign-per-type,
/// mandatory fields, and the recovery-reference rule.</summary>
public class ReconciliationRulesTests
{
    private static ReconInput In(bool delivered, bool billed, bool dup = false,
        decimal? billedAmt = null, decimal? contract = null, decimal? delivQty = null, decimal? billedQty = null) =>
        new(delivered, billed, dup, billedAmt, contract, delivQty, billedQty);

    [Fact]
    public void Both_delivered_and_billed_with_equal_amounts_is_matched() =>
        ReconClassifier.Classify(In(true, true, billedAmt: 100, contract: 100)).Should().Be(ReconBucket.Matched);

    [Fact]
    public void Billed_without_a_fulfillment_is_billed_not_delivered() =>
        ReconClassifier.Classify(In(false, true)).Should().Be(ReconBucket.BilledNotDelivered);

    [Fact]
    public void Delivered_without_a_bill_is_delivered_not_billed() =>
        ReconClassifier.Classify(In(true, false)).Should().Be(ReconBucket.DeliveredNotBilled);

    [Fact]
    public void Billed_differs_from_contract_is_price_variance() =>
        ReconClassifier.Classify(In(true, true, billedAmt: 200, contract: 180)).Should().Be(ReconBucket.PriceVariance);

    [Fact]
    public void Delivered_quantity_differs_from_billed_is_quantity_variance() =>
        ReconClassifier.Classify(In(true, true, billedAmt: 100, contract: 100, delivQty: 1, billedQty: 2))
            .Should().Be(ReconBucket.QuantityVariance);

    [Fact]
    public void A_duplicate_outranks_everything() =>
        ReconClassifier.Classify(In(false, true, dup: true)).Should().Be(ReconBucket.Duplicate);

    [Fact]
    public void Price_variance_outranks_quantity_variance_when_both_hold() =>
        ReconClassifier.Classify(In(true, true, billedAmt: 200, contract: 180, delivQty: 1, billedQty: 3))
            .Should().Be(ReconBucket.PriceVariance);

    // ---- adjustment rules ---------------------------------------------------------------------------------
    [Theory]
    [InlineData(AdjustmentType.Deduction, -1)]
    [InlineData(AdjustmentType.Recovery, -1)]
    [InlineData(AdjustmentType.Clawback, -1)]
    [InlineData(AdjustmentType.Writeoff, -1)]
    [InlineData(AdjustmentType.Reversal, -1)]
    [InlineData(AdjustmentType.Void, -1)]
    [InlineData(AdjustmentType.PriceCorrection, 0)]
    [InlineData(AdjustmentType.QuantityCorrection, 0)]
    [InlineData(AdjustmentType.Reallocation, 0)]
    public void Each_type_carries_its_required_sign(AdjustmentType type, int sign) =>
        AdjustmentRules.RequiredSign(type).Should().Be(sign);

    [Fact]
    public void A_zero_delta_is_rejected() =>
        AdjustmentRules.Validate(AdjustmentType.PriceCorrection, 0m, ReasonCodes.NoTariff, "x", null)
            .Should().Be("amount-delta-required");

    [Fact]
    public void A_writeoff_that_increases_payable_is_rejected() =>
        AdjustmentRules.Validate(AdjustmentType.Writeoff, 50m, ReasonCodes.LimitExceeded, "x", null)
            .Should().Be("sign-must-be-negative");

    [Fact]
    public void An_adjustment_without_rationale_is_rejected() =>
        AdjustmentRules.Validate(AdjustmentType.PriceCorrection, -20m, ReasonCodes.NoTariff, " ", null)
            .Should().Be("rationale-required");

    [Fact]
    public void An_unknown_reason_code_is_rejected() =>
        AdjustmentRules.Validate(AdjustmentType.PriceCorrection, -20m, "NOT_REAL", "x", null)
            .Should().Be("reason-code-required");

    [Fact]
    public void A_recovery_without_an_original_line_is_rejected() =>
        AdjustmentRules.Validate(AdjustmentType.Recovery, -20m, ReasonCodes.DuplicateClaim, "x", null)
            .Should().Be("recovery-reference-required");

    [Fact]
    public void A_recovery_with_an_original_line_passes() =>
        AdjustmentRules.Validate(AdjustmentType.Recovery, -20m, ReasonCodes.DuplicateClaim, "x", Guid.NewGuid())
            .Should().BeNull();

    [Fact]
    public void A_reversal_voids_the_line_every_other_type_adjusts_it()
    {
        AdjustmentRules.ResultingStatus(AdjustmentType.Reversal).Should().Be(ClaimLineStatus.Void);
        AdjustmentRules.ResultingStatus(AdjustmentType.Void).Should().Be(ClaimLineStatus.Void);
        AdjustmentRules.ResultingStatus(AdjustmentType.PriceCorrection).Should().Be(ClaimLineStatus.Adjusted);
    }
}
