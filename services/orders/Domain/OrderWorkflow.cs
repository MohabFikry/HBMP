namespace Mersal.Orders.Domain;

/// <summary>Canonical order transition table (23-state-machines §2). Phase 4.2 exercises create→Requested and
/// the route transitions (Requested→PendingApproval / Requested→Active) plus cancel; the consume transitions
/// (Active/PartiallyUsed→…) are enforced in phase 5. Illegal moves are rejected and audited as TransitionDenied.</summary>
public static class OrderWorkflow
{
    private static readonly Dictionary<OrderStatus, HashSet<OrderStatus>> Allowed = new()
    {
        [OrderStatus.Requested] = [OrderStatus.PendingApproval, OrderStatus.Active, OrderStatus.Cancelled],
        [OrderStatus.PendingApproval] = [OrderStatus.Approved, OrderStatus.Rejected, OrderStatus.Cancelled],
        [OrderStatus.Approved] = [OrderStatus.Active],
        [OrderStatus.Active] = [OrderStatus.PartiallyUsed, OrderStatus.Completed, OrderStatus.Expired, OrderStatus.Cancelled],
        // 18.A4: the self-loop is declared in 23 §2 — consuming a further subset leaves the order
        // PartiallyUsed and that is a legal, audited move, not a no-op.
        [OrderStatus.PartiallyUsed] = [OrderStatus.PartiallyUsed, OrderStatus.Completed, OrderStatus.Expired],
        [OrderStatus.Rejected] = [],
        [OrderStatus.Completed] = [],
        [OrderStatus.Expired] = [],
        [OrderStatus.Cancelled] = [],
    };

    public static bool CanTransition(OrderStatus from, OrderStatus to) =>
        Allowed.TryGetValue(from, out var tos) && tos.Contains(to);

    /// <summary>An order may be cancelled while not yet fully consumed (Requested / PendingApproval / Active).</summary>
    public static bool CanCancel(OrderStatus from) => CanTransition(from, OrderStatus.Cancelled);
}
