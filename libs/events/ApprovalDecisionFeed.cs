namespace Mersal.Events;

/// <summary>
/// The decision feed: authorization decisions MIRRORED back to the two services that own the thing decided
/// about, so an approval actually releases the order or prescription that was waiting for it.
///
/// <para><b>What was missing.</b> No service consumed <c>approvals.events</c>. orders-service declares
/// <c>PendingApproval → Approved → Active</c> in its transition table and pharmacy declares
/// <c>Submitted → Approved</c> in its, and nothing executed either: the only path that ever set a
/// prescription Approved was the auto-route at creation, and <c>IsDispensable</c> requires Approved — so a
/// prescription that went for approval could never be dispensed, no matter what the reviewer decided.
/// Rejection was worse than useless: the order stayed PendingApproval forever with no compensation, so
/// "rejected" and "still waiting" looked identical to everyone downstream.</para>
///
/// <para><b>Two queues, not one.</b> Point-to-point again: one shared queue would have RabbitMQ deal each
/// decision to orders OR pharmacy, so roughly half of each service's approvals would land on the other and be
/// discarded. Each owner gets its own copy.</para>
///
/// <para><b>Each queue receives every decision and filters by <c>source</c>.</b> The alternative — routing by
/// source at the relay — would put approvals' <c>AuthSource</c> vocabulary in the publisher and require it to
/// parse payloads to route them, which is the one thing a relay must not do. Filtering costs a discarded
/// message; mis-routing costs a decision that reaches nobody, and there is no third party to notice.</para>
///
/// <para><b>Why events and not an HTTP callback</b>, which is how a validity extension travels
/// (<c>HttpValidityExtensionApplier</c>). That one is synchronous on purpose: the reviewer must get both the
/// decision and the new expiry or neither, because an authorization reading Approved beside a prescription
/// the counter still refuses is a contradiction the pharmacist cannot resolve. An ordinary decision has no
/// such coupling — <c>Decisions.Decide</c> has always documented the release as something "consumers of the
/// emitted event" do — and making it synchronous would mean a reviewer could not reject a request while
/// orders-service was restarting.</para>
///
/// <para><b><c>AuthInfoRequested</c> is deliberately absent.</b> It lands on the same append-only ledger as
/// the others but it is a reviewer asking a question, not an answer: the order stays PendingApproval and the
/// prescription stays Submitted, which is already true, so a consumer would have nothing to do with it but
/// risk moving something.</para>
/// </summary>
public static class ApprovalDecisionFeed
{
    /// <summary>orders-service's own decision queue.</summary>
    public const string OrdersQueue = "orders.approval-decisions";

    /// <summary>pharmacy-service's own decision queue.</summary>
    public const string PharmacyQueue = "pharmacy.approval-decisions";

    public static readonly IReadOnlyList<string> Queues = [OrdersQueue, PharmacyQueue];

    /// <summary>
    /// The decisions that SETTLE a request — every one that moves the authorization to a terminal state.
    ///
    /// <para>Break-glass decisions (<c>AuthOverridden</c>, <c>AuthEmergencyApproved</c>) are on the list for
    /// the reason they exist: an emergency approval that did not release the order would leave the clinician
    /// who broke glass exactly where they were. The event carries <c>breakGlass</c> so the downstream audit
    /// record says which kind of decision released it.</para>
    /// </summary>
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "AuthApproved", "AuthPartiallyApproved", "AuthRejected", "AuthOverridden", "AuthEmergencyApproved",
    };

    public static bool Includes(string? eventType) => eventType is not null && Types.Contains(eventType);

    /// <summary>Exposed so both consumers' tests read this list rather than each keeping a copy.</summary>
    public static IReadOnlyCollection<string> EventTypes => Types;
}
