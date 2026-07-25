using FluentAssertions;
using Mersal.Orders.Domain;

namespace Mersal.Orders.Tests;

/// <summary>The pure consume rules (23-state-machines §2 "Atomic-consume guard"): partial consume leaves the
/// remainder Active and the order PartiallyUsed; consuming the remainder Completes it; a used line can never be
/// reused (no-reuse); cumulative consumed may not exceed ordered; only Active/PartiallyUsed orders are consumable.
/// Provider capability keeps a lab tech out of imaging work and vice-versa.</summary>
public class OrderConsumeTests
{
    private static InvestigationOrder Order(OrderStatus status, params OrderLine[] lines) => new()
    {
        OrderId = Guid.NewGuid(), OrderNo = "ORD-2026-000001", BeneficiaryId = Guid.NewGuid(), EncounterId = Guid.NewGuid(),
        OrderingProviderId = Guid.NewGuid(), OrderType = OrderType.Lab, Status = status, RequestedAt = DateTimeOffset.UtcNow,
        Lines = lines.ToList(),
    };

    private static OrderLine Line(decimal ordered, decimal consumed = 0, OrderLineStatus status = OrderLineStatus.Active) => new()
    {
        OrderLineId = Guid.NewGuid(), CodeSystem = CodeSystem.CPT, Code = "80053",
        QuantityOrdered = ordered, QuantityConsumed = consumed, Status = status,
    };

    [Fact]
    public void Partial_consume_leaves_remainder_active_and_order_partially_used()
    {
        var l1 = Line(1); var l2 = Line(1);
        var order = Order(OrderStatus.Active, l1, l2);

        OrderConsume.Validate(order, [new ConsumeLineRequest(l1.OrderLineId, 1)]).Should().Be(ConsumeError.None);
        OrderConsume.Apply(order, [new ConsumeLineRequest(l1.OrderLineId, 1)]);

        l1.Status.Should().Be(OrderLineStatus.Completed);
        l2.Status.Should().Be(OrderLineStatus.Active);
        order.Status.Should().Be(OrderStatus.PartiallyUsed);
    }

    [Fact]
    public void Consuming_all_lines_completes_the_order()
    {
        var l1 = Line(2); var order = Order(OrderStatus.Active, l1);
        OrderConsume.Apply(order, [new ConsumeLineRequest(l1.OrderLineId, 1)]);
        order.Status.Should().Be(OrderStatus.PartiallyUsed);
        l1.Status.Should().Be(OrderLineStatus.PartiallyUsed);

        OrderConsume.Validate(order, [new ConsumeLineRequest(l1.OrderLineId, 1)]).Should().Be(ConsumeError.None);
        OrderConsume.Apply(order, [new ConsumeLineRequest(l1.OrderLineId, 1)]);
        l1.Status.Should().Be(OrderLineStatus.Completed);
        order.Status.Should().Be(OrderStatus.Completed);
    }

    [Fact]
    public void A_used_line_cannot_be_reused()
    {
        var l1 = Line(1, consumed: 1, status: OrderLineStatus.Completed);
        var order = Order(OrderStatus.PartiallyUsed, l1, Line(1));
        OrderConsume.Validate(order, [new ConsumeLineRequest(l1.OrderLineId, 1)]).Should().Be(ConsumeError.AlreadyUsed);
    }

    [Fact]
    public void Cannot_consume_beyond_ordered_quantity()
    {
        var l1 = Line(1);
        var order = Order(OrderStatus.Active, l1);
        OrderConsume.Validate(order, [new ConsumeLineRequest(l1.OrderLineId, 2)]).Should().Be(ConsumeError.OverConsume);
    }

    [Fact]
    public void Cannot_consume_a_non_active_order()
    {
        var l1 = Line(1);
        var order = Order(OrderStatus.PendingApproval, l1);
        OrderConsume.Validate(order, [new ConsumeLineRequest(l1.OrderLineId, 1)]).Should().Be(ConsumeError.OrderNotConsumable);
    }

    [Theory]
    [InlineData("lab_tech", OrderType.Lab, true)]
    [InlineData("lab_tech", OrderType.Imaging, false)]
    [InlineData("imaging_tech", OrderType.Imaging, true)]
    [InlineData("imaging_tech", OrderType.Lab, false)]
    [InlineData("doctor", OrderType.Lab, false)]
    public void Provider_capability_matches_order_type(string role, OrderType type, bool expected) =>
        ProviderCapability.CanFulfil([role], type).Should().Be(expected);
}
