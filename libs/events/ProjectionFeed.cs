namespace Mersal.Events;

/// <summary>
/// The read-model feed: domain events that are MIRRORED to reporting-service in addition to their own stream.
///
/// <para><b>Why a mirror and not a subscription.</b> The transport is point-to-point — <see
/// cref="RabbitMqEventPublisher"/> publishes to the default exchange with the destination as the routing key,
/// so everything bound to a queue COMPETES for its messages. A reporting consumer attached to
/// <c>policy.events</c> would have RabbitMQ deal each event to either eligibility-service or reporting, never
/// both, and roughly half of every dashboard number would silently go missing. The same is true of
/// <c>orders.events</c> and <c>pharmacy.events</c>, which policy-service already consumes.</para>
///
/// <para><b>Why not a second copy from each publisher</b>, the way notification-service is fed. That pattern
/// is right when the publisher knows something the consumer cannot work out — notification needs the
/// RECIPIENT, and only the publishing service knows who is waiting. Reporting needs no such thing: it derives
/// everything from the event's own payload. Making thirteen call sites construct a reporting-shaped envelope
/// would teach thirteen services the read model's field vocabulary, and every schema change would then be a
/// thirteen-service change. Mirroring the raw message and letting reporting map it keeps that knowledge in
/// the one service it belongs to.</para>
///
/// <para><b>The mirror is additive.</b> The original publish is untouched, so no existing consumer sees any
/// difference; this is a second <c>BasicPublish</c> of the same body, with the same <c>MessageId</c>. The id
/// matters: reporting dedupes on it, so a redelivery — or a message that arrives twice because the relay
/// retried — is a no-op rather than a double-counted member.</para>
///
/// <para><b>Names are as PUBLISHED, not as projected.</b> Several differ (<c>EncounterStarted</c> →
/// EncounterCreated, <c>ApptBooked</c> → AppointmentBooked, <c>OrderLinesConsumed</c> → OrderLineConsumed).
/// The relay sees what is on the wire, so that is what this list holds; translating to the projector's
/// vocabulary is reporting's job and happens in <c>ProjectionMapping</c>. A test in reporting-service asserts
/// the two lists still correspond — this file and that switch drifting apart is exactly the failure the
/// §11 sweep exists to catch.</para>
/// </summary>
public static class ProjectionFeed
{
    /// <summary>reporting-service's own queue. Its own, so it competes with nobody.</summary>
    public const string Queue = "reporting.projection-events";

    /// <summary>
    /// The event types worth mirroring — those the reporting projectors actually consume.
    ///
    /// <para>An allow-list rather than "mirror everything": the platform publishes 104 distinct event types
    /// and reporting projects 20 of them. Mirroring the rest would put five times the traffic on a queue
    /// whose consumer would discard it, and — worse — would fill <c>processed_event</c> with ids that were
    /// never facts, so "have we seen this event?" would stop being a useful question.</para>
    /// </summary>
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        // approvals.events — the authorization worklist: pending queue, decisions, TAT and SLA breaches.
        "AuthSubmitted", "AuthUnderReview", "AuthApproved", "AuthPartiallyApproved", "AuthRejected",
        "AuthInfoRequested", "AuthOverridden", "AuthEmergencyApproved",

        // policy.events — the enrolment curve and the member utilization facts behind the analytics views.
        "MemberEnrolled", "MemberTerminated", "MemberReinstated", "MemberPlanChanged",
        "MemberGroupChanged", "MemberEnrolmentCancelled", "CoverageLimitChanged",

        // policy.events — the dimension LABELS. Without them the charts name a payer or a plan by the first
        // eight characters of its uuid, which is what `AnalyticsQueries.Label` falls back to.
        "PayerCreated", "PolicyPlanAttached",

        // emr.events — encounter and appointment counts.
        "EncounterStarted", "ApptBooked", "ApptCheckedIn", "ApptNoShow",

        // orders / pharmacy — what was actually delivered, by modality and by drug.
        "OrderLinesConsumed", "RxDispensed",

        // claims.events — the cost facts. `reporting.fact_cost` held zero rows before this feed existed.
        //
        // The TERMINAL decision, not `ClaimAdjudicated.v1`. Adjudication is a pre-decision recommendation, so
        // booking it as cost would count money a reviewer may still reduce — and then count it again when
        // they do. These names are `Claim{Status}.v1`, built by interpolation at the publisher, so the three
        // terminal statuses are listed rather than pattern-matched: a `.v1` suffix wildcard would also catch
        // ClaimSubmitted.v1 and ClaimCreated.v1, which are not costs.
        "ClaimApproved.v1", "ClaimPartiallyApproved.v1", "ClaimDenied.v1",

        // The same settlement BY SERVICE LINE — one event per settled line, feeding
        // `reporting.financial_fact`, which the phase-8.2 financial summary and the executive dashboard's
        // financial widget both read. That table was fed by `ServiceValued`, which nothing publishes, so
        // both returned zero from the day they were built. A claim-level event cannot serve the breakdown:
        // the grain is one claim and the question is per service line, and the projector reads scalars, so a
        // nested array would be invisible to it.
        "ClaimLineSettled.v1",
    };

    public static bool Includes(string? eventType) => eventType is not null && Types.Contains(eventType);

    /// <summary>Exposed so reporting-service's drift test can compare this against its own mapping.</summary>
    public static IReadOnlyCollection<string> EventTypes => Types;
}
