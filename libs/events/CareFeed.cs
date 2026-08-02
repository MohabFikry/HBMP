namespace Mersal.Events;

/// <summary>
/// The care-episode feed: domain events MIRRORED to emr-service so a visit's timeline can record what the
/// visit caused (ADR-0031).
///
/// <para><b>Why a mirror.</b> Same reason as <see cref="ProjectionFeed"/>, and worth restating because getting
/// it wrong here is silent: <see cref="RabbitMqEventPublisher"/> publishes to the default exchange with the
/// destination as the routing key, so every consumer bound to a queue COMPETES for its messages. An emr
/// consumer attached to <c>orders.events</c> would have RabbitMQ deal each event to either policy-service or
/// emr — never both — so half the benefit accumulator would stop moving AND half the timeline would go
/// missing, and neither service would log anything wrong. Its own queue means it competes with nobody.</para>
///
/// <para><b>Why a mirror and not a second enqueue at each call site</b>, the way notification-service is fed.
/// That pattern is right when the publisher knows something the consumer cannot work out — notification needs
/// the RECIPIENT, and only the publishing service knows who is waiting. emr needs no such thing: it resolves
/// the episode from the encounter id on the payload against its OWN encounter table, which is the one place
/// that authoritatively knows which appointment a visit belongs to and which member it is for. Teaching nine
/// call sites in three services to construct a timeline-shaped envelope would put emr's vocabulary in their
/// code and make every step-catalogue change a three-service change.</para>
///
/// <para><b>What the mirror requires of a publisher: <c>encounterId</c>.</b> Without it a step cannot be
/// attached to an episode, and a step attached to the wrong episode is worse than a missing one. Both
/// <c>orders.order</c> and <c>pharmacy.prescription</c> have held the column since phase 4 and simply never
/// put it on the wire — which is why "what did this consultation order?" had no answer. <see
/// cref="Mersal.Events.Tests"/>' architecture scan fails the build if a mirrored event's payload drops it.</para>
///
/// <para><b>Names are as PUBLISHED, not as stepped.</b> <c>OrderCreated</c> becomes the step
/// <c>OrderPlaced</c>, <c>OrderLinesConsumed</c> becomes <c>SampleConsumed</c>. The relay sees what is on the
/// wire, so that is what this list holds; translating to the step catalogue is emr's job and happens in
/// <c>CareEpisodeMapping</c>, which has a test asserting the two lists still correspond.</para>
/// </summary>
public static class CareFeed
{
    /// <summary>emr-service's own queue for the episode timeline. Distinct from <c>emr.events</c>, which is
    /// what emr PUBLISHES — a service consuming its own outbound stream is a loop waiting to be written.</summary>
    public const string Queue = "emr.care-episode-events";

    /// <summary>
    /// The event types that describe something a clinician did inside a visit.
    ///
    /// <para>An allow-list, not "mirror everything from the three services". A step is a thing that happened
    /// TO THE PATIENT in an episode — <c>OrderActivated</c> and <c>RxApproved</c> are the routing policy
    /// saying it did NOT need an approval, which no desk and no clinician would recognise as an event in
    /// their day, and putting them on a timeline would bury the steps that matter under ones that do not.</para>
    ///
    /// <para><b>The one event here that is not unconditionally a step: <c>RxSubmitted</c>.</b> Pharmacy emits
    /// it for every prescription, carrying <c>requiresApproval</c>, so it is on the feed but becomes a step
    /// only when that flag is set — see <c>CareEpisodeMapping</c>. It earns its place because it is the ONLY
    /// event pharmacy publishes when a prescription goes for approval, and a gated prescription that read
    /// identically to a ready one is precisely the case a desk gets asked about.</para>
    /// </summary>
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        // orders.events — the investigation leg: ordered, routed for approval, cancelled, sample taken,
        // result back.
        "OrderCreated", "OrderPendingApproval", "OrderCancelled", "OrderLinesConsumed", "OrderResultUploaded",

        // pharmacy.events — the medication leg: written, routed for approval, cancelled, handed over.
        "RxCreated", "RxSubmitted", "RxCancelled", "RxLinesDispensed",

        // approvals.events — the DECISION only.
        //
        // `AuthSubmitted` is deliberately absent: the order or prescription already recorded that it went for
        // approval, and a second "sent for approval" step from the other side of the same seam reads as two
        // separate requests to whoever is looking at the timeline.
        //
        // `AuthInfoRequested` is absent for a sharper reason. It lands on the same append-only decision ledger
        // as the others, so it is tempting to treat it as one — but it is a reviewer asking for more
        // information, not an answer. A desk shown "authorization decided" stops chasing, and this is exactly
        // the case where somebody must keep chasing.
        "AuthApproved", "AuthPartiallyApproved", "AuthRejected", "AuthOverridden", "AuthEmergencyApproved",
    };

    public static bool Includes(string? eventType) => eventType is not null && Types.Contains(eventType);

    /// <summary>Exposed so emr-service's mapping test and the payload architecture scan can both read the
    /// same list rather than each keeping a copy that drifts.</summary>
    public static IReadOnlyCollection<string> EventTypes => Types;
}
