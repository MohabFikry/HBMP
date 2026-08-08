namespace Mersal.Orders.Domain;

/// <summary>Which order types a fulfilling provider role may act on (min-necessary capability, 11-permission-matrix):
/// a lab tech fulfils Lab orders, a radiology tech fulfils Radiology orders. Anything else is out of that role's lane
/// and must be refused — the queue never surfaces it and consume rejects it with an audited 403.
///
/// <para>29.1 — both spellings of the radiology role and both spellings of the order type resolve here for the
/// duration of the rename window (design 45 §1). A principal reaching this point has already been expanded by
/// <c>LegacyRoleAliases</c>, so in practice only the order-type side matters; it is written explicitly anyway,
/// because a capability map that silently answers "no" is indistinguishable from a correct refusal, and this
/// one decides whether a technician can work.</para></summary>
public static class ProviderCapability
{
    public static IReadOnlySet<OrderType> ForRoles(IEnumerable<string> roles)
    {
        var caps = new HashSet<OrderType>();
        foreach (var r in roles)
        {
            if (string.Equals(r, "lab_tech", StringComparison.Ordinal)) caps.Add(OrderType.Lab);
            else if (string.Equals(r, "radiology_tech", StringComparison.Ordinal)
                  || string.Equals(r, "imaging_tech", StringComparison.Ordinal)) caps.Add(OrderType.Radiology);
        }
        return caps;
    }

    public static bool CanFulfil(IEnumerable<string> roles, OrderType type) =>
        ForRoles(roles).Contains(OrderTypes.Canonical(type));
}

/// <summary>Why a consume request was refused (mapped to problem+json at the edge). <c>None</c> means it passed
/// validation and may be applied.</summary>
public enum ConsumeError
{
    None, InvalidQuantity, LineNotFound, AlreadyUsed, OverConsume, OrderNotConsumable,
    /// <summary>Past its validity window. Distinct from <see cref="OrderNotConsumable"/> because the recovery
    /// is different and specific: an expired order can be revalidated by the approval team, whereas a
    /// cancelled one is finished.</summary>
    OrderExpired,
}

public sealed record ConsumeLineRequest(Guid OrderLineId, decimal Quantity);

/// <summary>The pure consume rules (23-state-machines §2 "Atomic-consume guard"): an order may be consumed only in
/// Active/PartiallyUsed; a used (Completed/Cancelled) line can NEVER be consumed again (no-reuse); cumulative
/// consumed may never exceed ordered. Validation is separated from the mutation so it is unit-testable without a DB;
/// the atomic/idempotent/duplicate-proof guarantees are enforced at the datastore (unique key + xmin + CHECK).</summary>
public static class OrderConsume
{
    public static ConsumeError Validate(
        InvestigationOrder order, IReadOnlyList<ConsumeLineRequest> requests, DateTimeOffset? now = null)
    {
        if (requests.Count == 0) return ConsumeError.LineNotFound;
        if (order.Status is not (OrderStatus.Active or OrderStatus.PartiallyUsed))
            return ConsumeError.OrderNotConsumable;

        /*
         * PAST ITS VALIDITY WINDOW.
         *
         * This rule did not exist. `expires_at` was in the schema from migration 0001, the index on it too,
         * and nothing ever checked it here — so once orders started carrying an expiry, an order could lapse
         * and still be fulfilled, and the whole mechanism was decoration. pharmacy's dispense rule has always
         * compared the date; this is the missing twin.
         *
         * Checked against the CLOCK and not only against the status, because the expiry sweeper runs hourly:
         * between lapsing and being swept an order still reads Active, and a status-only test would leave an
         * hour a day in which expired orders are fulfillable.
         */
        if (now is { } clock && order.ExpiresAt is { } expiry && expiry <= clock)
            return ConsumeError.OrderExpired;

        foreach (var req in requests)
        {
            if (req.Quantity <= 0) return ConsumeError.InvalidQuantity;
            var line = order.Lines.FirstOrDefault(l => l.OrderLineId == req.OrderLineId);
            if (line is null) return ConsumeError.LineNotFound;
            // no-reuse. Superseded joins the set in 30.1: the line was AMENDED, and consuming it would
            // deliver the version the doctor withdrew — the code they corrected, the quantity they reduced.
            // The amendment would have achieved nothing, and the record would say it had.
            if (line.IsTerminal) return ConsumeError.AlreadyUsed;
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

    /// <summary>Order rolls up from its lines: all live lines Completed ⇒ Completed; any consumption yet
    /// work remaining ⇒ PartiallyUsed; otherwise unchanged.
    ///
    /// <para>A line that has LEFT the live set does not hold the order open. Cancelled has always been
    /// excluded; 30.1 adds Superseded, and it matters more: a superseded line is never Completed — it was
    /// replaced, not delivered — so counting it would strand the order in PartiallyUsed for ever,
    /// <c>OrderCompleted</c> would never emit, and the order would sit in a technician's queue with nothing
    /// left to do on it. Its consumption is not counted either, because the successor carries the
    /// accumulator forward and counting the dead row again would double it.</para></summary>
    public static OrderStatus RecomputeOrderStatus(InvestigationOrder order)
    {
        var live = order.Lines
            .Where(l => l.Status is not (OrderLineStatus.Cancelled or OrderLineStatus.Superseded)).ToList();
        if (live.Count > 0 && live.All(l => l.Status == OrderLineStatus.Completed))
            return OrderStatus.Completed;
        if (live.Any(l => l.QuantityConsumed > 0))
            return OrderStatus.PartiallyUsed;
        return order.Status;
    }
}
