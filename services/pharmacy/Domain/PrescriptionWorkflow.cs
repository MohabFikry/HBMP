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
        [RxStatus.Approved] = [RxStatus.PartiallyDispensed, RxStatus.Dispensed, RxStatus.Expired, RxStatus.Cancelled],
        [RxStatus.PartiallyDispensed] = [RxStatus.Dispensed, RxStatus.Expired],
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
}
