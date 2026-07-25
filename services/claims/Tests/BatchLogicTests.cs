using FluentAssertions;
using Mersal.Claims.Domain;

namespace Mersal.Claims.Tests;

/// <summary>Pure-domain unit tests for batching: the lifecycle transition table (23 §9) and rollup arithmetic.</summary>
public class BatchLogicTests
{
    [Theory]
    [InlineData(BatchStatus.Open, BatchStatus.UnderReview, true)]
    [InlineData(BatchStatus.Open, BatchStatus.Cancelled, true)]
    [InlineData(BatchStatus.UnderReview, BatchStatus.Decided, true)]
    [InlineData(BatchStatus.UnderReview, BatchStatus.Open, true)]
    [InlineData(BatchStatus.Decided, BatchStatus.SettlementIssued, true)]
    [InlineData(BatchStatus.SettlementIssued, BatchStatus.Closed, true)]
    [InlineData(BatchStatus.Open, BatchStatus.Decided, false)]        // must go through review
    [InlineData(BatchStatus.Decided, BatchStatus.Open, false)]        // no reopening a decided batch
    [InlineData(BatchStatus.Closed, BatchStatus.Open, false)]
    [InlineData(BatchStatus.SettlementIssued, BatchStatus.Cancelled, false)]
    public void Transition_table_matches_the_state_machine(BatchStatus from, BatchStatus to, bool allowed) =>
        BatchTransitions.CanTransition(from, to).Should().Be(allowed);

    [Fact]
    public void Rollup_sums_approved_denied_and_ignores_void()
    {
        var lines = new[]
        {
            Line(ClaimLineStatus.Approved, billed: 200, price: 180, allowed: 180),
            Line(ClaimLineStatus.PartiallyApproved, billed: 300, price: 250, allowed: 150),
            Line(ClaimLineStatus.Denied, billed: 100, price: 90, allowed: null),
            Line(ClaimLineStatus.Void, billed: 999, price: 999, allowed: 999), // excluded entirely
        };
        var r = BatchRollup.Compute(lines);
        r.Claimed.Should().Be(600);   // 200 + 300 + 100 (void excluded)
        r.Priced.Should().Be(520);    // 180 + 250 + 90
        r.Approved.Should().Be(330);  // 180 + 150
        r.Denied.Should().Be(100);
        r.NetPayable.Should().Be(330);
    }

    [Fact]
    public void Rollup_nets_signed_adjustments_into_net_payable()
    {
        var lines = new[] { Line(ClaimLineStatus.Approved, billed: 200, price: 180, allowed: 180) };
        var r = BatchRollup.Compute(lines, adjusted: -30m);
        r.Adjusted.Should().Be(-30m);
        r.NetPayable.Should().Be(150m); // 180 approved − 30 adjusted
    }

    private static ClaimLine Line(ClaimLineStatus status, decimal billed, decimal price, decimal? allowed) => new()
    {
        ClaimLineId = Guid.NewGuid(), Code = "80053", CodeSystem = ClaimCodeSystem.CPT, Quantity = 1,
        Status = status, BilledAmount = billed, ContractPrice = price, AllowedAmount = allowed,
    };
}
