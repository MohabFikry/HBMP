namespace Mersal.Orders.Domain;

/// <summary>
/// 29.2b — whether an EXTERNAL provider may see or act on a given order (design 45 §2b).
///
/// <para><b>This is written as a pure function, separately from any gate, so the two-provider test can pin it
/// before a queue endpoint exists.</b> That ordering is the point. The DispensingGate defect (audit R3) was
/// not a subtle logic error — it was that the ownership question was asked of the caller's own token rather
/// than of the row, and no test ever posed the question "can provider A see provider B's work?", because
/// answering it requires two providers and every test had one.</para>
///
/// <para><b>Fail-closed on absence.</b> An order with no assignment belongs to no external provider. A null
/// owner must never read as "unowned, therefore visible" — that is the same shape as the defect, reached by a
/// different route: a queue that shows everything nobody has claimed.</para>
/// </summary>
public static class ProviderOwnership
{
    /// <summary>
    /// May <paramref name="callerProviderId"/> see/act on an order assigned to
    /// <paramref name="assignedProviderId"/>?
    /// </summary>
    /// <remarks>
    /// Both arguments are required to be non-empty. A caller with no provider identity is not an external
    /// provider at all, and an order with no assignment is not external work. Neither is an error to be
    /// tolerated: both are refusals.
    /// </remarks>
    public static bool MayAccess(Guid? callerProviderId, Guid? assignedProviderId) =>
        callerProviderId is { } caller && caller != Guid.Empty
        && assignedProviderId is { } owner && owner != Guid.Empty
        && caller == owner;

    /// <summary>Filter a set of orders to the caller's own. The queue is built from THIS, not from a status
    /// filter with an ownership check bolted on afterwards — a filter that can be forgotten in one query is a
    /// filter that will be.</summary>
    public static IEnumerable<InvestigationOrder> OwnedBy(
        IEnumerable<InvestigationOrder> orders, Guid? callerProviderId)
    {
        ArgumentNullException.ThrowIfNull(orders);
        return orders.Where(o => MayAccess(callerProviderId, o.AssignedProviderId));
    }
}
