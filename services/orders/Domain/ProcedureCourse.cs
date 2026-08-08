namespace Mersal.Orders.Domain;

/// <summary>
/// 31.1 — an OP-Procedure order as ONE COURSE: one kind, one session count, a quantity per session.
///
/// <para><b>What this replaces.</b> Design 45 §2 held that "sessions ARE the quantity", with the kind and the
/// count on each LINE. That cannot express an outpatient course: a physiotherapy referral is one clinical
/// decision that may involve several billable items per attendance, and under the old model a two-item course
/// could be composed as six sessions of one item and eight of the other — not a course any centre can
/// deliver. There was also nowhere to record "three of these each time", because the quantity slot was
/// already spent on the session count.</para>
///
/// <para><b>What deliberately did NOT change.</b> <c>OrderLine.QuantityOrdered</c> is still the METERED
/// TOTAL — what the atomic consume path decrements, what a partial approval narrows, and what the delivering
/// centre's queue counts down. It is now <c>sessions x per-session</c>. Sessions delivered is DERIVED from it
/// here rather than stored, because a second stored counter that could disagree with the first is precisely
/// the "parallel counter" §2 was right to forbid.</para>
/// </summary>
public static class ProcedureCourse
{
    /// <summary>
    /// The quantity a line actually meters: the course length times what is delivered at each attendance.
    /// </summary>
    /// <param name="sessions">
    /// NULL when the procedure type is not delivered in sessions — which is a different fact from 1, and the
    /// reason it is nullable rather than defaulted. The composer shows no session field for such a type, and
    /// storing 1 would make "not session-based" indistinguishable from "a one-session course".
    /// </param>
    public static decimal MeteredTotal(int? sessions, decimal quantityPerSession) =>
        sessions is { } n && n > 0 ? n * quantityPerSession : quantityPerSession;

    /// <summary>
    /// Progress in SESSIONS — "4 of 6 sessions delivered", the same sentence at both ends of the course.
    /// </summary>
    /// <remarks>
    /// <para>Authorised comes from <c>QuantityOrdered</c>, which a partial approval has already narrowed, and
    /// never from what was requested. Ten sessions delivered against a six-session approval reads as a
    /// completed course in every view; nothing would be out of balance for anyone to notice.</para>
    ///
    /// <para>Delivered rounds DOWN. Three units of a two-per-session item is one completed attendance and
    /// half of another, and rounding up reports a session the patient has not had.</para>
    /// </remarks>
    public static (int Delivered, int Authorised) SessionProgress(OrderLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        // A CHECK constraint forbids zero, but legacy rows and hand-edited data do not respect intentions,
        // and a divide-by-zero here would take down the centre's whole queue rather than one malformed order.
        var per = line.QuantityPerSession > 0 ? line.QuantityPerSession : 1m;

        return ((int)Math.Floor(line.QuantityConsumed / per), (int)Math.Floor(line.QuantityOrdered / per));
    }
}
