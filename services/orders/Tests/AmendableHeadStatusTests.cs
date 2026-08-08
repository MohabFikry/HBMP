using FluentAssertions;
using Mersal.Orders.Domain;

namespace Mersal.Orders.Tests;

/// <summary>
/// 30.2 — which HEAD statuses permit their lines to be cancelled or amended.
///
/// <para>This exposed a defect that predates phase 30. <see cref="OrderStatus.PartiallyUsed"/> had no
/// transition to <see cref="OrderStatus.Cancelled"/> at all, so
/// <c>POST /investigation-orders/{id}/cancel</c> answered 409 for <b>every partly-fulfilled order</b>. A
/// doctor with a three-line order whose first sample had been taken could not withdraw the other two —
/// which is precisely the case design 46 §3 opens with ("3-line prescription, line 1 dispensed → lines 2
/// and 3 only"). The order-level endpoint has always refused the whole request rather than doing what it
/// could, and nothing said so.</para>
/// </summary>
public class AmendableHeadStatusTests
{
    [Fact]
    public void A_partly_fulfilled_order_can_still_be_cancelled()
    {
        // The fix. Without it, the amendable scope of a partly-consumed order is empty — the exact opposite
        // of design 46 §3, whose whole point is that the REMAINDER stays amendable.
        OrderWorkflow.CanCancel(OrderStatus.PartiallyUsed).Should().BeTrue(
            "the unconsumed remainder of a partly-fulfilled order is what amendment is FOR");
    }

    [Theory]
    [InlineData(OrderStatus.Requested)]
    [InlineData(OrderStatus.PendingApproval)]
    [InlineData(OrderStatus.Approved)]
    [InlineData(OrderStatus.Active)]
    [InlineData(OrderStatus.PartiallyUsed)]
    public void An_unfinished_order_permits_its_lines_to_change(OrderStatus status) =>
        OrderWorkflow.CanAmendLines(status).Should().BeTrue();

    [Theory]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.Cancelled)]
    public void A_finished_order_does_not(OrderStatus status) =>
        OrderWorkflow.CanAmendLines(status).Should().BeFalse();

    [Fact]
    public void Amendability_is_asked_separately_from_cancellability()
    {
        // Approved-but-not-yet-Active is the case that separates them: the order cannot be CANCELLED from
        // that status under the 23 §2 table, but its lines are still unfulfilled and still correctable.
        // Folding the two questions into one would have made a doctor's correction depend on whether the
        // approval callback had landed yet.
        OrderWorkflow.CanAmendLines(OrderStatus.Approved).Should().BeTrue();
        OrderWorkflow.CanCancel(OrderStatus.Approved).Should().BeFalse();
    }
}
