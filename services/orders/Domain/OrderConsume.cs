namespace Mersal.Orders.Domain;

/// <summary>Which order types a fulfilling provider role may act on (min-necessary capability, 11-permission-matrix):
/// a lab tech fulfils Lab orders, an imaging tech fulfils Imaging orders. Anything else is out of that role's lane
/// and must be refused — the queue never surfaces it and consume rejects it with an audited 403.</summary>
public static class ProviderCapability
{
    public static IReadOnlySet<OrderType> ForRoles(IEnumerable<string> roles)
    {
        var caps = new HashSet<OrderType>();
        foreach (var r in roles)
        {
            if (string.Equals(r, "lab_tech", StringComparison.Ordinal)) caps.Add(OrderType.Lab);
            else if (string.Equals(r, "imaging_tech", StringComparison.Ordinal)) caps.Add(OrderType.Imaging);
        }
        return caps;
    }

    public static bool CanFulfil(IEnumerable<string> roles, OrderType type) => ForRoles(roles).Contains(type);
}

/// <summary>Why a consume request was refused (mapped to problem+json at the edge). <c>None</c> means it passed
/// validation and may be applied.</summary>
public enum ConsumeError { None, InvalidQuantity, LineNotFound, AlreadyUsed, OverConsume, OrderNotConsumable }

public sealed record ConsumeLineRequest(Guid OrderLineId, decimal Quantity);

/// <summary>The pure consume rules (23-state-machines §2 "Atomic-consume guard"): an order may be consumed only in
/// Active/PartiallyUsed; a used (Completed/Cancelled) line can NEVER be consumed again (no-reuse); cumulative
/// consumed may never exceed ordered. Validation is separated from the mutation so it is unit-testable without a DB;
/// the atomic/idempotent/duplicate-proof guarantees are enforced at the datastore (unique key + xmin + CHECK).</summary>
public static class OrderConsume
{
    public static ConsumeError Validate(InvestigationOrder order, IReadOnlyList<ConsumeLineRequest> requests)
    {
        if (requests.Count == 0) return ConsumeError.LineNotFound;
        if (order.Status is not (OrderStatus.Active or OrderStatus.PartiallyUsed))
            return ConsumeError.OrderNotConsumable;

        foreach (var req in requests)
        {
            if (req.Quantity <= 0) return ConsumeError.InvalidQuantity;
            var line = order.Lines.FirstOrDefault(l => l.OrderLineId == req.OrderLineId);
            if (line is null) return ConsumeError.LineNotFound;
            if (line.Status is OrderLineStatus.Completed or OrderLineStatus.Cancelled)
                return ConsumeError.AlreadyUsed;                                   // no-reuse
            if (line.QuantityConsumed + req.Quantity > line.QuantityOrdered)
                return ConsumeError.OverConsume;
        }
        return ConsumeError.None;
    }

    /// <summary>Advance the accumulator and recompute line + order status. Caller has already validated.</summary>
    public static void Apply(InvestigationOrder order, IReadOnlyList<ConsumeLineRequest> requests)
    {
        foreach (var req in requests)
        {
            var line = order.Lines.First(l => l.OrderLineId == req.OrderLineId);
            line.QuantityConsumed += req.Quantity;
            line.Status = line.QuantityConsumed >= line.QuantityOrdered
                ? OrderLineStatus.Completed
                : OrderLineStatus.PartiallyUsed;
        }
        order.Status = RecomputeOrderStatus(order);
    }

    /// <summary>Order rolls up from its lines: all non-cancelled lines Completed ⇒ Completed; any consumption yet
    /// work remaining ⇒ PartiallyUsed; otherwise unchanged. Cancelled lines don't hold the order open.</summary>
    public static OrderStatus RecomputeOrderStatus(InvestigationOrder order)
    {
        var live = order.Lines.Where(l => l.Status != OrderLineStatus.Cancelled).ToList();
        if (live.Count > 0 && live.All(l => l.Status == OrderLineStatus.Completed))
            return OrderStatus.Completed;
        if (live.Any(l => l.QuantityConsumed > 0))
            return OrderStatus.PartiallyUsed;
        return order.Status;
    }
}
