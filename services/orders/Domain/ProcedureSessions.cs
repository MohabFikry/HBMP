namespace Mersal.Orders.Domain;

/// <summary>
/// 29.2 — how an approval decision becomes a DELIVERABLE session count (design 45 §2).
///
/// <para>"Sessions authorised ≠ sessions requested. If the doctor asks for ten and the approval team
/// partially approves six, the deliverable count is six. The session count must flow from the APPROVED scope,
/// never the requested one."</para>
///
/// <para>Kept as pure domain, separate from the endpoint, because it is the single arithmetic step in this
/// gate that is easy to get backwards and expensive when it is: reading the requested count over-supplies the
/// beneficiary by the difference and over-consumes their benefit by the same, and it does so SILENTLY — ten
/// sessions delivered against a six-session approval reads as a completed course in the centre's queue and in
/// the ordering doctor's worklist alike. Nothing is out of balance for anyone to notice.</para>
/// </summary>
public static class ProcedureSessions
{
    /// <summary>
    /// The quantity a line may actually deliver, given what was requested and what was approved.
    ///
    /// <para>Three rules, all of them fail-safe downward:</para>
    /// <list type="bullet">
    /// <item><b>No approval decision yet ⇒ 0.</b> Not "the requested amount, pending". An order awaiting a
    /// decision entitles the beneficiary to nothing, and treating absence as assent is how a full course gets
    /// delivered against an authorisation that never existed.</item>
    /// <item><b>Approved below the request ⇒ the approved amount.</b> The whole point.</item>
    /// <item><b>Approved above the request ⇒ the request.</b> An approval cannot grant more than was asked
    /// for; if the data says otherwise that is a defect upstream, and the safe reading is the smaller number.</item>
    /// </list>
    /// </summary>
    public static decimal Deliverable(decimal requested, decimal? approved)
    {
        if (requested <= 0) return 0;
        if (approved is not { } a) return 0;
        return Math.Max(0, Math.Min(requested, a));
    }

    /// <summary>
    /// Whether a line whose approval has landed still has anything to deliver, and how much.
    ///
    /// <para>Applied when an approval decision is recorded: <c>QuantityOrdered</c> — what consume meters
    /// against — is narrowed to the deliverable amount, while <c>RequestedQuantity</c> is left alone so that
    /// "how often do we approve less than we ask for?" stays an answerable question.</para>
    /// </summary>
    public static void ApplyApproval(OrderLine line, decimal? approvedQuantity)
    {
        ArgumentNullException.ThrowIfNull(line);

        var deliverable = Deliverable(line.RequestedQuantity, approvedQuantity);

        // Never below what has ALREADY been consumed. A retroactive approval cut that went under the delivered
        // count would violate quantity_consumed <= quantity_ordered and, worse, would imply un-delivering a
        // session a beneficiary has already attended. The overage is a case for the approvals team, not
        // something to resolve by rewriting history.
        line.QuantityOrdered = Math.Max(line.QuantityConsumed, deliverable);

        if (line.QuantityOrdered == 0) line.Status = OrderLineStatus.Cancelled;
        else if (line.QuantityConsumed >= line.QuantityOrdered) line.Status = OrderLineStatus.Completed;
    }

    /// <summary>Progress for both views — the centre's queue and the ordering doctor's worklist show the same
    /// sentence ("4 of 6 sessions delivered"), because a course that reads differently at each end is a course
    /// somebody will deliver twice.</summary>
    public static (int Delivered, int Authorised) Progress(OrderLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return ((int)line.QuantityConsumed, (int)line.QuantityOrdered);
    }
}
