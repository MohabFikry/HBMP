using FluentAssertions;
using Mersal.Orders.Domain;

namespace Mersal.Orders.Tests;

/// <summary>
/// 30.1 — <see cref="OrderLineStatus.Superseded"/> is a new terminal line status, and two rules that already
/// existed have to learn about it. Neither would fail loudly if it did not.
///
/// <para><b>The roll-up.</b> <see cref="OrderConsume.RecomputeOrderStatus"/> treats every non-Cancelled line
/// as live. A superseded line is never Completed — it was replaced, not delivered — so an order carrying one
/// would sit in PartiallyUsed for ever, <c>OrderCompleted</c> would never emit, and the order would stay in
/// a technician's queue with nothing left to do on it. Exactly the strand ADR-0034's compare-and-set roll-up
/// was written to fix, arriving by a different door.</para>
///
/// <para><b>The consume guard.</b> A superseded line has been replaced by a corrected one. Consuming it
/// would deliver the version the doctor withdrew — the drug they changed, the quantity they reduced — and
/// the amendment would have achieved nothing.</para>
/// </summary>
public class SupersededLineRollupTests
{
    private static OrderLine Line(OrderLineStatus status, decimal ordered = 2, decimal consumed = 0) => new()
    {
        OrderLineId = Guid.NewGuid(), Code = "80053", CodeSystem = CodeSystem.CPT,
        QuantityOrdered = ordered, RequestedQuantity = ordered, QuantityConsumed = consumed, Status = status,
    };

    private static InvestigationOrder Order(params OrderLine[] lines) => new()
    {
        OrderId = Guid.NewGuid(), OrderType = OrderType.Lab, Status = OrderStatus.PartiallyUsed,
        Lines = [.. lines],
    };

    [Fact]
    public void A_superseded_line_does_not_hold_the_order_open()
    {
        // Line 1 was amended away; line 2 was delivered. There is nothing left to do, so the order is
        // Completed. Without this the order strands in PartiallyUsed and never emits OrderCompleted.
        var order = Order(
            Line(OrderLineStatus.Superseded),
            Line(OrderLineStatus.Completed, ordered: 2, consumed: 2));

        OrderConsume.RecomputeOrderStatus(order).Should().Be(OrderStatus.Completed);
    }

    [Fact]
    public void An_order_whose_every_line_was_superseded_or_cancelled_is_not_Completed()
    {
        // Nothing was delivered, so "Completed" would be a false statement about a patient's care. The
        // order keeps its current status and the cancel path — not the consume roll-up — closes it.
        var order = Order(Line(OrderLineStatus.Superseded), Line(OrderLineStatus.Cancelled));

        OrderConsume.RecomputeOrderStatus(order).Should().Be(OrderStatus.PartiallyUsed);
    }

    [Fact]
    public void A_superseded_line_that_had_been_partly_consumed_does_not_report_the_order_PartiallyUsed()
    {
        // The successor carries the accumulator forward, so counting the dead row's consumption again
        // would let a superseded line alone keep an untouched order looking half-delivered.
        var order = Order(Line(OrderLineStatus.Superseded, ordered: 6, consumed: 4));
        order.Status = OrderStatus.Active;

        OrderConsume.RecomputeOrderStatus(order).Should().Be(OrderStatus.Active);
    }

    [Fact]
    public void A_superseded_line_can_never_be_consumed()
    {
        var line = Line(OrderLineStatus.Superseded);
        var order = Order(line);
        order.Status = OrderStatus.Active;

        OrderConsume.Validate(order, [new ConsumeLineRequest(line.OrderLineId, 1)])
            .Should().Be(ConsumeError.AlreadyUsed,
                "consuming a superseded line delivers the version the doctor withdrew");
    }

    [Fact]
    public void IsTerminal_covers_every_status_a_line_never_leaves()
    {
        Line(OrderLineStatus.Completed).IsTerminal.Should().BeTrue();
        Line(OrderLineStatus.Cancelled).IsTerminal.Should().BeTrue();
        Line(OrderLineStatus.Superseded).IsTerminal.Should().BeTrue();
        Line(OrderLineStatus.Active).IsTerminal.Should().BeFalse();
        Line(OrderLineStatus.PartiallyUsed).IsTerminal.Should().BeFalse();
    }
}
