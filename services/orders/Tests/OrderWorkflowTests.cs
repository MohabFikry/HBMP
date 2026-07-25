using FluentAssertions;
using Mersal.Orders.Domain;

namespace Mersal.Orders.Tests;

/// <summary>Canonical order transition table (23-state-machines §2) + order-number formatting.</summary>
public class OrderWorkflowTests
{
    [Theory]
    [InlineData(OrderStatus.Requested, OrderStatus.PendingApproval, true)]
    [InlineData(OrderStatus.Requested, OrderStatus.Active, true)]
    [InlineData(OrderStatus.PendingApproval, OrderStatus.Approved, true)]
    [InlineData(OrderStatus.PendingApproval, OrderStatus.Rejected, true)]
    [InlineData(OrderStatus.Approved, OrderStatus.Active, true)]
    [InlineData(OrderStatus.Active, OrderStatus.Completed, true)]
    [InlineData(OrderStatus.Requested, OrderStatus.Completed, false)]      // must be Active first
    [InlineData(OrderStatus.Rejected, OrderStatus.Active, false)]          // terminal
    [InlineData(OrderStatus.Completed, OrderStatus.Cancelled, false)]      // terminal
    public void Transition_legality(OrderStatus from, OrderStatus to, bool legal) =>
        OrderWorkflow.CanTransition(from, to).Should().Be(legal);

    [Theory]
    [InlineData(OrderStatus.Requested, true)]
    [InlineData(OrderStatus.PendingApproval, true)]
    [InlineData(OrderStatus.Active, true)]
    [InlineData(OrderStatus.Completed, false)]
    [InlineData(OrderStatus.Cancelled, false)]
    public void Cancel_guard(OrderStatus from, bool canCancel) =>
        OrderWorkflow.CanCancel(from).Should().Be(canCancel);

    [Fact]
    public void Order_no_is_formatted() =>
        OrderNo.Format(2026, 42).Should().Be("ORD-2026-000042");
}
