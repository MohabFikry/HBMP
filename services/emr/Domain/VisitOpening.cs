namespace Mersal.Emr.Domain;

/// <summary>How a visit began, and it is deliberately three cases rather than a nullable timestamp.</summary>
public enum VisitOpeningKind
{
    /// <summary>Checked in, then seen. The normal path: both entries, both actors, waiting time shown.</summary>
    CheckedInThenSeen,

    /// <summary>
    /// NO CHECK-IN RECORDED — a walk-in taken straight into the room, or a missed step.
    ///
    /// <para>The timeline says so in words. It does NOT silently begin at Visit started as though the two
    /// were the same moment: absence of a record is not evidence the step happened.</para>
    /// </summary>
    NoCheckInRecorded,

    /// <summary>
    /// The check-in is timestamped AFTER the visit started — a retroactive entry, and a real data-quality
    /// signal.
    ///
    /// <para>Both are shown AS RECORDED and the inconsistency is flagged. They are not reordered into a
    /// plausible sequence: silently sorting bad timestamps into a tidy story is how you lose the ability to
    /// notice the process is broken.</para>
    /// </summary>
    RecordedOutOfOrder,
}

/// <param name="Waiting">Visit start minus check-in. NULL whenever it cannot honestly be computed — no
/// check-in recorded, or the two are out of order, where the "wait" would be negative and a reader would
/// have no way to tell a real short wait from a data error.</param>
public sealed record VisitOpening(
    VisitOpeningKind Kind, DateTimeOffset? CheckedInAt, DateTimeOffset? VisitStartedAt, TimeSpan? Waiting)
{
    /// <summary>True when the pair should be surfaced as a data-quality problem rather than a measurement.</summary>
    public bool Flagged => Kind == VisitOpeningKind.RecordedOutOfOrder;
}

/// <summary>
/// 30.5c — how the encounter timeline opens (design 46 §7c).
///
/// <para>The timeline currently begins at <b>Visit started</b>. It should begin at <b>Checked in</b>, then
/// Visit started, then everything that follows — which means joining two aggregates: check-in lives on
/// <c>emr.appointment</c> and the encounter begins later. This is the pure part of that composition, so the
/// three cases can be proven without a database.</para>
///
/// <para><b>The byproduct is the point.</b> <c>visit started − checked in</c> is the patient's waiting time,
/// and once the two moments sit on one timeline it costs nothing. That number is the one a clinic manager
/// actually wants.</para>
/// </summary>
public static class VisitOpeningRules
{
    public static VisitOpening Compose(DateTimeOffset? checkedInAt, DateTimeOffset? visitStartedAt)
    {
        // No arrival recorded. Reported as its own case rather than as a null waiting time, because "we do
        // not know how long they waited" and "they did not wait" are different statements and only one of
        // them is true.
        if (checkedInAt is not { } arrived)
            return new VisitOpening(VisitOpeningKind.NoCheckInRecorded, null, visitStartedAt, null);

        if (visitStartedAt is not { } started)
            return new VisitOpening(VisitOpeningKind.CheckedInThenSeen, arrived, null, null);

        // Recorded out of order. NOT swapped, NOT clamped to zero, and the waiting time is withheld: a
        // negative interval rendered as "0 minutes" is a data error wearing a measurement's clothes, and it
        // would be averaged into a dashboard alongside real waits.
        if (arrived > started)
            return new VisitOpening(VisitOpeningKind.RecordedOutOfOrder, arrived, started, null);

        return new VisitOpening(
            VisitOpeningKind.CheckedInThenSeen, arrived, started, started - arrived);
    }
}
