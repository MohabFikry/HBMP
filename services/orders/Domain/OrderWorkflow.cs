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
        // 30.4: PendingApproval ADDED. An amendment that leaves the APPROVED SCOPE (design 46 §5) sends the
        // order back for review — the authorisation's basis no longer holds, and leaving it Active would be
        // a way to get approval for one thing and have another performed.
        [OrderStatus.Active] =
            [OrderStatus.PartiallyUsed, OrderStatus.Completed, OrderStatus.Expired, OrderStatus.Cancelled,
             OrderStatus.PendingApproval],
        // 18.A4: the self-loop is declared in 23 §2 — consuming a further subset leaves the order
        // PartiallyUsed and that is a legal, audited move, not a no-op.
        //
        // 30.2: Cancelled ADDED. Its absence was a defect that predates phase 30 and was invisible because
        // it presented as a 409 on a path nobody had a reason to exercise: a partly-fulfilled order could
        // not be cancelled AT ALL, so a doctor with a three-line order whose first sample had been taken
        // could not withdraw the other two. That is the case design 46 §3 opens with, and the endpoint has
        // always refused the whole request rather than doing what it could.
        [OrderStatus.PartiallyUsed] =
            [OrderStatus.PartiallyUsed, OrderStatus.Completed, OrderStatus.Expired, OrderStatus.Cancelled,
             OrderStatus.PendingApproval],
        [OrderStatus.Rejected] = [],
        [OrderStatus.Completed] = [],
        [OrderStatus.Expired] = [],
        [OrderStatus.Cancelled] = [],
    };

    public static bool CanTransition(OrderStatus from, OrderStatus to) =>
        Allowed.TryGetValue(from, out var tos) && tos.Contains(to);

    /// <summary>An order may be cancelled while not yet fully consumed.</summary>
    public static bool CanCancel(OrderStatus from) => CanTransition(from, OrderStatus.Cancelled);

    /// <summary>
    /// 30.2 — may this order's LINES still be cancelled or amended (design 46 §3)?
    ///
    /// <para>A different question from <see cref="CanCancel"/>, and asked separately on purpose. Approved
    /// is the status that separates them: the 23 §2 table does not permit the ORDER to move from Approved
    /// to Cancelled — it moves to Active first — but its lines are unfulfilled and still correctable.
    /// Folding the two into one would make a doctor's ability to fix a mistake depend on whether the
    /// approval callback had landed yet, which is not a clinical distinction.</para>
    /// </summary>
    public static bool CanAmendLines(OrderStatus from) => from
        is OrderStatus.Requested or OrderStatus.PendingApproval or OrderStatus.Approved
        or OrderStatus.Active or OrderStatus.PartiallyUsed;
}
