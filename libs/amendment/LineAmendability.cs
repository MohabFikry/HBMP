namespace Mersal.Amendment;

/// <summary>
/// A line reduced to the four facts that decide whether it may be cancelled or amended. Deliberately NOT the
/// service's own entity: <c>OrderLine</c> and <c>PrescriptionLine</c> carry different status enums and
/// different clinical fields, and design 46 §3's rule is one rule about both. Passing the entities in would
/// force this library to depend on two services, or force the rule to be written twice.
/// </summary>
/// <param name="LineId">Which line, so a refusal can name it.</param>
/// <param name="IsTerminal">The line has reached a status it never leaves — fully consumed, cancelled or
/// superseded. Each service maps its own enum; what matters here is that the line is finished.</param>
/// <param name="Quantity">What may be delivered against the line (for a session-based procedure, sessions).</param>
/// <param name="Consumed">What already has been. This is the part that is fact.</param>
public readonly record struct AmendableLine(Guid LineId, bool IsTerminal, decimal Quantity, decimal Consumed);

/// <summary>The order- or prescription-level facts every line on it shares.</summary>
/// <param name="HeadAmendable">The head is in a status from which its lines may still change.</param>
/// <param name="Expired">Past its validity window.</param>
public readonly record struct AmendContext(bool HeadAmendable, bool Expired);

/// <summary>Why a cancel or amend was refused. <c>None</c> means it may be applied.</summary>
public enum AmendabilityError
{
    None,

    /// <summary>The line is finished — fully consumed, already cancelled, or already superseded. Design 46
    /// §3: "Line 1 is fact." Nothing here can be withdrawn, because it has already happened.</summary>
    AlreadyTerminal,

    /// <summary>The head is in a status whose lines may not change (rejected, completed, cancelled).</summary>
    OrderNotAmendable,

    /// <summary>Past its validity window. Its own error rather than folded into
    /// <see cref="OrderNotAmendable"/>, because the recovery differs and the doctor can act on it: an
    /// expired order can be revalidated by the approval team, and design 46 §7 is explicit that an expired
    /// order "is not amendable, it is expired".</summary>
    Expired,

    /// <summary>The amended total is below what has already been consumed, which implies un-dispensing.
    /// Invariant 4.</summary>
    BelowConsumed,

    /// <summary>Zero or negative. That is a cancellation, and it has its own path with its own event.</summary>
    InvalidQuantity,

    /// <summary>The amendment leaves the line exactly as it was. Superseding a signed record, burning a
    /// version and notifying four parties to say nothing changed is worse than refusing.</summary>
    NoChange,
}

/// <summary>
/// The pure amendable-scope rule (design 46 §3): <b>the amendable scope is whatever has not been consumed.</b>
///
/// <para>No I/O, no clock, no database. The atomic guarantee — that the line has not moved between this
/// answer and the write — is Gate 2's guarded UPDATE, not this. What lives here is the question of whether
/// the request makes sense at all, so it can be answered identically by the doctor's screen before they
/// commit and by the server before it writes.</para>
/// </summary>
public static class LineAmendability
{
    /// <summary>
    /// May this line be cancelled?
    ///
    /// <para>A PARTLY consumed line may. Cancelling it forfeits the remainder and leaves what was delivered
    /// standing — design 46 §3's "6-session physiotherapy, 4 delivered → reduce to 4 delivered + 2
    /// cancelled". The consumed accumulator is never touched, so nothing about the four sessions changes;
    /// the line simply stops being fulfillable.</para>
    /// </summary>
    public static AmendabilityError ForCancel(AmendableLine line, AmendContext ctx)
    {
        // Order of evaluation is deliberate, and the LINE is asked before the head.
        //
        // Expiry first, because it is the widest and most recoverable reason: a doctor whose order has
        // merely lapsed should be told that, not that the line is finished.
        //
        // Then the line's own state, because on a single-line order the head status is a CONSEQUENCE of it —
        // consuming the only line completes the order — and "line 2 was performed at 14:32" is a fact the
        // doctor can act on where "the order is not amendable" restates their own request back at them. When
        // the head is genuinely the problem (a cancelled order carrying a live line) that answer still
        // surfaces, because the line is not terminal.
        if (ctx.Expired) return AmendabilityError.Expired;
        if (line.IsTerminal) return AmendabilityError.AlreadyTerminal;
        if (!ctx.HeadAmendable) return AmendabilityError.OrderNotAmendable;
        return AmendabilityError.None;
    }

    /// <summary>
    /// May this line be amended to <paramref name="newQuantity"/>?
    ///
    /// <para>An INCREASE is allowed here. Whether it needs a fresh authorisation is design 46 §5's question
    /// and Gate 4's code; refusing it at this layer would make an unapproved order unamendable while an
    /// approved one stayed amendable, which is the pair §5 warns is costly to get backwards.</para>
    /// </summary>
    public static AmendabilityError ForAmend(AmendableLine line, decimal newQuantity, AmendContext ctx)
    {
        if (ctx.Expired) return AmendabilityError.Expired;
        if (line.IsTerminal) return AmendabilityError.AlreadyTerminal;
        if (!ctx.HeadAmendable) return AmendabilityError.OrderNotAmendable;
        if (newQuantity <= 0) return AmendabilityError.InvalidQuantity;

        // Checked BEFORE NoChange, so amending a fully-delivered-but-still-open line down to what was
        // delivered reads as the legal instruction it is rather than as "you changed nothing".
        if (newQuantity < line.Consumed) return AmendabilityError.BelowConsumed;
        if (newQuantity == line.Quantity) return AmendabilityError.NoChange;
        return AmendabilityError.None;
    }
}
