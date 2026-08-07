namespace Mersal.Pharmacy.Domain;

/// <summary>Canonical prescription transition table (23-state-machines §3). Phase 4.3 exercises
/// create→Draft→Submitted and the route decision (Submitted→Approved auto, or stays Submitted for approvals) plus
/// cancel; dispensing transitions are phase 6. Illegal moves are rejected + audited as TransitionDenied.</summary>
public static class PrescriptionWorkflow
{
    private static readonly Dictionary<RxStatus, HashSet<RxStatus>> Allowed = new()
    {
        [RxStatus.Draft] = [RxStatus.Submitted, RxStatus.Cancelled],
        [RxStatus.Submitted] = [RxStatus.Approved, RxStatus.Rejected, RxStatus.Cancelled],
        // 30.4: Submitted ADDED — an out-of-scope amendment sends the script back for review. Submitted is
        // the awaiting-approval state, and IsDispensable excludes it, so the counter refuses it until a
        // reviewer has looked at what changed (design 46 §5).
        [RxStatus.Approved] =
            [RxStatus.PartiallyDispensed, RxStatus.Dispensed, RxStatus.Expired, RxStatus.Cancelled,
             RxStatus.Submitted],
        // 18.A4: declared self-loop in 23 §3 — a further partial dispense leaves the Rx PartiallyDispensed.
        //
        // 30.2: Cancelled ADDED, and its absence was the same defect orders carried. A partly-dispensed
        // prescription could not be cancelled AT ALL, so a doctor whose three-line script had had its first
        // drug handed over could not withdraw the other two — design 46 §3's opening example, unreachable.
        [RxStatus.PartiallyDispensed] =
            [RxStatus.PartiallyDispensed, RxStatus.Dispensed, RxStatus.Expired, RxStatus.Cancelled,
             RxStatus.Submitted],
        [RxStatus.Rejected] = [],
        [RxStatus.Dispensed] = [],
        [RxStatus.Expired] = [],
        [RxStatus.Cancelled] = [],
    };

    public static bool CanTransition(RxStatus from, RxStatus to) =>
        Allowed.TryGetValue(from, out var tos) && tos.Contains(to);

    /// <summary>Cancellable while not fully dispensed (Draft / Submitted / Approved).</summary>
    public static bool CanCancel(RxStatus from) => CanTransition(from, RxStatus.Cancelled);

    /// <summary>A prescription is dispensable only once Approved (phase 6 acts on this).</summary>
    public static bool IsDispensable(RxStatus status) => status is RxStatus.Approved or RxStatus.PartiallyDispensed;

    /// <summary>
    /// 30.2 — may this prescription's LINES still be cancelled or amended (design 46 §3)?
    ///
    /// <para>Asked separately from <see cref="CanCancel"/> for the reason orders' <c>CanAmendLines</c>
    /// records: a Draft or Submitted script is not yet approved but its lines are certainly still
    /// correctable, and a doctor's ability to fix a dose must not depend on where the approval queue has
    /// got to.</para>
    /// </summary>
    public static bool CanAmendLines(RxStatus from) => from
        is RxStatus.Draft or RxStatus.Submitted or RxStatus.Approved or RxStatus.PartiallyDispensed;
}
