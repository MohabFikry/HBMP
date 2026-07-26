using FluentAssertions;
using Mersal.Claims.Domain;

namespace Mersal.Claims.Tests;

/// <summary>Pure-domain unit tests for reimbursement rules (10b.6, 36 §3.3): the OCR-match decision (confidence
/// high/low/boundary, ambiguity, mismatch, missing authorization) and the min(tariff, receipt) cap + override path.
/// OCR is assistive — every one of these routes to ManualAssessment unless ALL conditions for AutoMatched hold.</summary>
public class ReimbursementRulesTests
{
    private const decimal Th = ReimbursementRules.DefaultConfidenceThreshold; // 0.90

    [Fact]
    public void High_confidence_unambiguous_no_mismatch_auto_matches() =>
        ReimbursementRules.DecideMatch(true, 1, false, [0.95m, 0.99m], Th).Should().Be(OcrMatchOutcome.AutoMatched);

    [Fact]
    public void At_the_threshold_boundary_it_still_auto_matches() =>
        ReimbursementRules.DecideMatch(true, 1, false, [Th, Th], Th).Should().Be(OcrMatchOutcome.AutoMatched);

    [Fact]
    public void Just_below_the_threshold_routes_to_manual() =>
        ReimbursementRules.DecideMatch(true, 1, false, [Th - 0.0001m], Th).Should().Be(OcrMatchOutcome.ManualAssessment);

    [Fact]
    public void More_than_one_candidate_is_ambiguous_and_routes_to_manual() =>
        ReimbursementRules.DecideMatch(true, 2, false, [0.99m], Th).Should().Be(OcrMatchOutcome.ManualAssessment);

    [Fact]
    public void A_mismatch_routes_to_manual_even_at_high_confidence() =>
        ReimbursementRules.DecideMatch(true, 1, true, [0.99m], Th).Should().Be(OcrMatchOutcome.ManualAssessment);

    [Fact]
    public void No_authorized_order_cannot_auto_match() =>
        ReimbursementRules.DecideMatch(false, 0, false, [0.99m], Th).Should().Be(OcrMatchOutcome.ManualAssessment);

    [Fact]
    public void No_extracted_fields_cannot_auto_match() =>
        ReimbursementRules.DecideMatch(true, 1, false, [], Th).Should().Be(OcrMatchOutcome.ManualAssessment);

    // ---- cap + override -----------------------------------------------------------------------------------
    [Theory]
    [InlineData(180, 200, 180)] // tariff lower → cap = tariff
    [InlineData(250, 200, 200)] // receipt lower → cap = receipt
    public void Cap_is_the_lesser_of_tariff_and_receipt(decimal tariff, decimal receipt, decimal expected) =>
        ReimbursementRules.Cap(tariff, receipt).Should().Be(expected);

    [Fact]
    public void With_no_tariff_the_receipt_is_the_ceiling() =>
        ReimbursementRules.Cap(null, 200m).Should().Be(200m);

    [Fact]
    public void Paying_at_or_under_the_cap_needs_no_override() =>
        ReimbursementRules.ValidateOverride(180m, 180m, isOverride: false, justification: null).Should().BeNull();

    [Fact]
    public void Paying_above_the_cap_without_override_is_rejected() =>
        ReimbursementRules.ValidateOverride(200m, 180m, isOverride: false, justification: null).Should().Be("exceeds-cap");

    [Fact]
    public void An_override_above_the_cap_requires_justification() =>
        ReimbursementRules.ValidateOverride(200m, 180m, isOverride: true, justification: " ").Should().Be("override-justification-required");

    [Fact]
    public void An_override_above_the_cap_with_justification_is_allowed() =>
        ReimbursementRules.ValidateOverride(200m, 180m, isOverride: true, justification: "hardship, approved").Should().BeNull();
}
