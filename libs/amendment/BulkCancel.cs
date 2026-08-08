namespace Mersal.Amendment;

/// <summary>What happened to one line in a whole-order cancel.</summary>
public sealed record LineCancelOutcome(Guid LineId, AmendabilityError Error)
{
    public bool Cancellable => Error == AmendabilityError.None;
}

/// <summary>
/// The plan for a whole-order cancel, and its shape is the point.
///
/// <para>Three outcomes, kept distinct because collapsing any two of them misinforms the doctor:
/// everything cancelled; SOME cancelled and the rest named with reasons; nothing cancelled. The middle one
/// is the case design 46 §3 is written about, and the two ways of getting it wrong are failing the whole
/// request (so a doctor who dispensed one of three lines cannot withdraw the other two at all) and silently
/// doing half (so they believe they have).</para>
///
/// <para><see cref="IsCompleteRefusal"/> exists so an empty result is never dressed as a success. A 200 with
/// an empty cancelled-list reads as "done" on a screen, and the doctor walks away believing an order was
/// withdrawn that is still live and still in a pharmacy's queue.</para>
/// </summary>
public sealed record BulkCancelPlan(IReadOnlyList<LineCancelOutcome> Outcomes)
{
    public IReadOnlyList<LineCancelOutcome> Cancellable => [.. Outcomes.Where(o => o.Cancellable)];
    public IReadOnlyList<LineCancelOutcome> Refusals => [.. Outcomes.Where(o => !o.Cancellable)];

    public int Applied => Cancellable.Count;
    public int Refused => Refusals.Count;

    /// <summary>Some lines cancelled and some refused — the case that must be reported plainly.</summary>
    public bool IsPartial => Applied > 0 && Refused > 0;

    /// <summary>Nothing could be cancelled. Includes an order with no lines: "there was nothing to cancel"
    /// is a refusal, not a success.</summary>
    public bool IsCompleteRefusal => Applied == 0;
}

/// <summary>
/// Design 46 §3 — "a whole-order cancel is simply 'cancel every still-cancellable line'".
///
/// <para>It is a per-line evaluation and nothing more; there is no separate order-level rule. That is the
/// design decision, not an implementation shortcut: an order-level rule would need its own notion of when a
/// partly-consumed order is cancellable, and the two notions would disagree the first time somebody changed
/// one of them.</para>
/// </summary>
public static class BulkCancel
{
    public static BulkCancelPlan Plan(IReadOnlyList<AmendableLine> lines, AmendContext ctx) =>
        new([.. lines.Select(l => new LineCancelOutcome(l.LineId, LineAmendability.ForCancel(l, ctx)))]);
}
