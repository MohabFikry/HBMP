using FluentAssertions;
using Mersal.Claims.Domain;

namespace Mersal.Claims.Tests;

/// <summary>Pure-domain unit tests for line decisions: mandatory reason/rationale + allowed-amount bounds, the line
/// effect of each decision, and the roll-up to claim status (23 §7/§8).</summary>
public class DecisionLogicTests
{
    [Fact]
    public void Deny_without_a_reason_code_is_rejected() =>
        DecisionRules.Validate(ClaimDecisionKind.Deny, null, [], "because", 200, 180, false)
            .Should().Be("reason-code-required");

    [Fact]
    public void Deny_without_rationale_is_rejected() =>
        DecisionRules.Validate(ClaimDecisionKind.Deny, null, [ReasonCodes.LimitExceeded], " ", 200, 180, false)
            .Should().Be("rationale-required");

    [Fact]
    public void Partial_without_allowed_amount_is_rejected() =>
        DecisionRules.Validate(ClaimDecisionKind.PartiallyApprove, null, [ReasonCodes.ExceedsAuthScope], "x", 200, 180, false)
            .Should().Be("allowed-amount-required");

    [Fact]
    public void Partial_above_the_cap_is_rejected() =>
        DecisionRules.Validate(ClaimDecisionKind.PartiallyApprove, 500m, [ReasonCodes.ExceedsAuthScope], "x", 200, 180, false)
            .Should().Be("allowed-exceeds-cap");

    [Fact]
    public void An_unknown_reason_code_is_rejected() =>
        DecisionRules.Validate(ClaimDecisionKind.Deny, null, ["NOT_REAL"], "x", 200, 180, false)
            .Should().Be("unknown-reason-code");

    [Fact]
    public void A_valid_approve_passes() =>
        DecisionRules.Validate(ClaimDecisionKind.Approve, null, [], null, 200, 180, false).Should().BeNull();

    [Fact]
    public void An_override_makes_rationale_mandatory_even_on_approve() =>
        DecisionRules.Validate(ClaimDecisionKind.Approve, 180m, [], null, 200, 180, isOverride: true)
            .Should().Be("rationale-required");

    [Fact]
    public void Approve_closes_the_line_at_the_lesser_of_billed_and_tariff() =>
        DecisionRules.Apply(ClaimDecisionKind.Approve, null, 200, 180).Should().Be((ClaimLineStatus.Approved, 180m));

    [Fact]
    public void Deny_closes_the_line_at_zero() =>
        DecisionRules.Apply(ClaimDecisionKind.Deny, null, 200, 180).Should().Be((ClaimLineStatus.Denied, 0m));

    [Fact]
    public void Request_info_and_route_to_clinical_do_not_close_the_line()
    {
        DecisionRules.Apply(ClaimDecisionKind.RequestInfo, null, 200, 180).Should().BeNull();
        DecisionRules.Apply(ClaimDecisionKind.RouteToClinical, null, 200, 180).Should().BeNull();
    }

    [Theory]
    [InlineData(new[] { ClaimLineStatus.Approved, ClaimLineStatus.Approved }, ClaimStatus.Approved)]
    [InlineData(new[] { ClaimLineStatus.Denied, ClaimLineStatus.Denied }, ClaimStatus.Denied)]
    [InlineData(new[] { ClaimLineStatus.Approved, ClaimLineStatus.Denied }, ClaimStatus.PartiallyApproved)]
    [InlineData(new[] { ClaimLineStatus.Approved, ClaimLineStatus.Pending }, ClaimStatus.UnderAdjudication)]
    public void Line_statuses_roll_up_to_the_claim_status(ClaimLineStatus[] lines, ClaimStatus expected) =>
        DecisionRules.RollUp(lines).Should().Be(expected);
}
