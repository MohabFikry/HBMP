using FluentAssertions;
using Mersal.Orders.Domain;

namespace Mersal.Orders.Tests;

/// <summary>Routing decision (US-032): gated type / gated code / high-cost → approval; otherwise auto-activate.</summary>
public class OrderRoutingTests
{
    private static InvestigationOrder Order(OrderType type, params (string code, decimal qty)[] lines) => new()
    {
        OrderId = Guid.NewGuid(), OrderType = type,
        Lines = lines.Select(l => new OrderLine
        {
            OrderLineId = Guid.NewGuid(), CodeSystem = CodeSystem.CPT, Code = l.code, QuantityOrdered = l.qty,
        }).ToList(),
    };

    [Fact]
    public void Gated_order_type_routes_to_approval()
    {
        var opts = new OrderRoutingOptions { GatedOrderTypes = { "Imaging" } };
        OrderRoutingPolicy.Evaluate(Order(OrderType.Imaging, ("70450", 1)), opts).RouteToApproval.Should().BeTrue();
    }

    [Fact]
    public void Ungated_lab_auto_activates()
    {
        var opts = new OrderRoutingOptions { GatedOrderTypes = { "Imaging" } };
        var d = OrderRoutingPolicy.Evaluate(Order(OrderType.Lab, ("80053", 1)), opts);
        d.RouteToApproval.Should().BeFalse();
        d.Reason.Should().Be("auto-activate");
    }

    [Fact]
    public void Gated_code_routes_to_approval_regardless_of_type()
    {
        var opts = new OrderRoutingOptions { GatedCodes = { "99999" } };
        OrderRoutingPolicy.Evaluate(Order(OrderType.Lab, ("80053", 1), ("99999", 1)), opts).RouteToApproval.Should().BeTrue();
    }

    [Fact]
    public void High_cost_estimate_routes_to_approval()
    {
        var opts = new OrderRoutingOptions
        {
            HighCostThreshold = 1000m,
            UnitCosts = { ["EXPENSIVE"] = 600m },
        };
        // 2 × 600 = 1200 ≥ 1000 → approval
        OrderRoutingPolicy.Evaluate(Order(OrderType.Lab, ("EXPENSIVE", 2)), opts).RouteToApproval.Should().BeTrue();
    }

    [Fact]
    public void Below_threshold_auto_activates()
    {
        var opts = new OrderRoutingOptions { HighCostThreshold = 1000m, UnitCosts = { ["CHEAP"] = 100m } };
        OrderRoutingPolicy.Evaluate(Order(OrderType.Lab, ("CHEAP", 3)), opts).RouteToApproval.Should().BeFalse();
    }
}
