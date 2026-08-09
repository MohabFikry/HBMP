namespace Mersal.Events;

/// <summary>
/// The routing feed: the two events that mean "a clinician asked for something that needs a decision",
/// MIRRORED to approvals-service so the request reaches the reviewer worklist.
///
/// <para><b>What was missing.</b> orders-service published <c>OrderPendingApproval</c> to
/// <c>orders.events</c> and pharmacy-service published <c>RxSubmitted</c> to <c>pharmacy.events</c>, and
/// approvals-service consumed neither. The ingestion seam it would have called —
/// <c>POST /api/v1/authorizations</c>, scope <c>auth:ingest</c> — had no caller anywhere in the platform. So
/// a gated order or prescription changed status, told the patient to wait, and then reached nobody: the
/// authorization it was waiting for was never created, and the only way one appeared was a human raising a
/// manual entry. Both services documented the consumer as future work; this is it.</para>
///
/// <para><b>Why a mirror rather than a second enqueue at each call site</b>, which is how
/// <c>FulfilmentRecorded</c> reaches <c>approvals.fulfilments</c>. That pattern is right when the producer
/// knows something the consumer cannot work out — a fulfilment carries the delivered items, which only the
/// dispensing service holds. Routing needs nothing of the kind: the two events already carry everything an
/// authorization is created from, because emr's care-episode feed made them carry the tenant, the encounter
/// and the ordering clinician. A second enqueue would be a second thing to forget at the fourth call site
/// (both services re-emit these on an out-of-scope amendment — design 46 §5), and forgetting it there is
/// exactly the silent half-wiring this feed exists to remove.</para>
///
/// <para><b>Why its own queue.</b> The transport is point-to-point. policy-service already consumes
/// <c>orders.events</c> and <c>pharmacy.events</c> for the benefit accumulator, so a second consumer bound to
/// either would COMPETE — each event would reach one service and not the other, and the accumulator would
/// silently stop moving for every event approvals happened to win.</para>
///
/// <para><b><c>RxSubmitted</c> is conditional.</b> pharmacy emits it for EVERY prescription and carries
/// <c>requiresApproval</c>; only the gated ones become authorizations. That filter lives in the consumer, not
/// here, for the same reason <c>CareFeed</c> keeps its own version of it in <c>CareEpisodeMapping</c>: the
/// relay routes by event type and has no business reading payloads.</para>
/// </summary>
public static class ApprovalRoutingFeed
{
    /// <summary>approvals-service's own routing queue. Distinct from <c>approvals.fulfilments</c>, which
    /// carries what was DELIVERED — a register, not a question. Distinct again from <c>approvals.events</c>,
    /// which is what approvals PUBLISHES.</summary>
    public const string Queue = "approvals.routing-events";

    /// <summary>The two events that route something for a decision.</summary>
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "OrderPendingApproval",   // orders.events — a gated investigation order (23 §2)
        "RxSubmitted",            // pharmacy.events — a prescription, gated ONLY when requiresApproval (23 §3)
    };

    public static bool Includes(string? eventType) => eventType is not null && Types.Contains(eventType);

    /// <summary>Exposed so the consumer and the architecture scan read this list rather than each keeping a
    /// copy that drifts.</summary>
    public static IReadOnlyCollection<string> EventTypes => Types;
}
