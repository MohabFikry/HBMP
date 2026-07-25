namespace Mersal.Orders.Domain;

/// <summary>Config-driven routing policy (US-032): a new order either routes to approval (high-cost / gated
/// service) or auto-activates. Phase-4 starts with a policy/cost threshold that is configuration-driven —
/// gated order types, an explicit gated-code list, and a per-order estimated-cost threshold — so the rule can
/// be tuned without a code change and later be sourced from masterdata/policy pricing.</summary>
public sealed class OrderRoutingOptions
{
    /// <summary>Order types always routed to approval (e.g. Imaging, Procedure). Empty = none by type.</summary>
    public HashSet<string> GatedOrderTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Specific codes (any system) always routed to approval regardless of cost.</summary>
    public HashSet<string> GatedCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-code estimated unit cost (config stand-in for masterdata pricing). Missing = 0.</summary>
    public Dictionary<string, decimal> UnitCosts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Estimated total order cost at/above which the order routes to approval. 0 disables the threshold.</summary>
    public decimal HighCostThreshold { get; set; }
}

public sealed record RoutingDecision(bool RouteToApproval, string Reason);

public static class OrderRoutingPolicy
{
    /// <summary>Decide whether <paramref name="order"/> must go to approval. Any gated type, any gated line code,
    /// or an estimated total cost ≥ threshold routes it; otherwise it auto-activates.</summary>
    public static RoutingDecision Evaluate(InvestigationOrder order, OrderRoutingOptions opts)
    {
        if (opts.GatedOrderTypes.Contains(order.OrderType.ToString()))
            return new RoutingDecision(true, $"order-type-gated:{order.OrderType}");

        var gatedLine = order.Lines.FirstOrDefault(l => opts.GatedCodes.Contains(l.Code));
        if (gatedLine is not null)
            return new RoutingDecision(true, $"gated-code:{gatedLine.Code}");

        if (opts.HighCostThreshold > 0)
        {
            var estimate = order.Lines.Sum(l =>
                (opts.UnitCosts.TryGetValue(l.Code, out var c) ? c : 0m) * l.QuantityOrdered);
            if (estimate >= opts.HighCostThreshold)
                return new RoutingDecision(true, $"high-cost:{estimate}>={opts.HighCostThreshold}");
        }

        return new RoutingDecision(false, "auto-activate");
    }
}
