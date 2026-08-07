using Mersal.Events;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;

namespace Mersal.Orders.Api;

/// <summary>
/// 30.2/30.5 — the PAYLOADS an amendment publishes, and the reason each destination exists.
///
/// <para><b>This class builds payloads. It does not enqueue.</b> The <c>EnqueueAsync</c> calls stay at the
/// call sites, inside the executor's <c>insideTransaction:</c> callbacks, because
/// <c>OutboxAtomicityTests</c> reads one file at a time and cannot follow a helper into another. Wrapping
/// the enqueue in a method here made the scan report both files as non-transactional debt — correctly, from
/// what it can see. A rule that load-bearing must stay able to see the thing it checks, so the shared part
/// is the payload and the enqueue stays where the transaction is.</para>
///
/// <para><b>Every event has a real subscriber, and the subscriber is named.</b> The platform already carries
/// a pile of published-and-unheard event types, and the pile grows one reasonable-looking call site at a
/// time.</para>
///
/// <list type="bullet">
/// <item><c>orders.events</c> → policy-service's accumulator reads it, and the <see cref="CareFeed"/> relay
/// mirrors it to emr-service's episode timeline. The accumulator ignores these two types deliberately — a
/// cancellation consumes nothing — but the timeline records them, which is the point: a doctor reading a
/// visit must see that an order was withdrawn.</item>
/// <item><c>notification.domain-events</c> → notification-service, which is template-driven and needs no new
/// consumer. A SECOND, differently-shaped copy rather than a second consumer on the same queue: the
/// transport is point-to-point, so binding notification to <c>orders.events</c> would have RabbitMQ deal
/// each message to either policy or notification, and half the benefit accumulator would silently stop.</item>
/// </list>
///
/// <para><b>Why the fulfilling provider is not on this list.</b> Their queue is a live query over
/// <c>orders.order_line</c> — see <c>Queue.AvailableOrders</c> and <c>ProcedureProvider.Owned</c> — so a
/// cancelled line leaves it in the SAME TRANSACTION as the cancellation. Invariant 6 is satisfied
/// structurally rather than eventually, which is stronger than an event could make it. The notification is
/// additional, exactly as design 46 §6 requires.</para>
/// </summary>
public static class AmendmentEvents
{
    // THE PAYLOADS ARE INLINE AT THE CALL SITES TOO, and the domain-payload builders that used to live here
    // are gone. CareFeedEnvelopeArchitectureTests and TenantOnEnvelopeArchitectureTests both read the
    // anonymous object that FOLLOWS the queue argument, to prove `encounterId` and `tenantId` are on the
    // wire. A helper hid both from them — and a mirrored event missing its encounter does not fail, warn or
    // dead-letter: the consumer correctly declines to place the step, acks, and the timeline is quietly
    // missing the order. That is the exact defect this scan was written for. The duplication is the price of
    // the check being able to see what it checks.
    //
    // What remains here is the NOTIFICATION payload, which no scan reads because the notification queue is
    // not tenant-bound in the same way and carries no episode.
    //
    // The names are LITERALS at the enqueue sites, not these constants. CareFeedEnvelopeArchitectureTests
    // scans source for a mirrored event's name beside its payload to prove `encounterId` is on the wire — a
    // step without it has no episode, and a step on the WRONG episode is worse than a missing one. A constant
    // hides the name from that scan, exactly as a helper method hid the enqueue from OutboxAtomicityTests.
    // These remain as the catalogue, and are what non-scanned code should reference.
    public const string LineCancelled = "OrderLineCancelled";
    public const string LineAmended = "OrderLineAmended";

    public const string DomainStream = "orders.events";
    public const string NotificationQueue = "notification.domain-events";

    /// <summary>
    /// 30.4 — an amendment that leaves the approved scope republishes THE EVENT THE ORIGINAL ROUTING USED
    /// (design 46 §5).
    ///
    /// <para>Deliberately not a new event type. `approvals.fulfilments` parses every message as a
    /// <c>FulfilmentMessage</c> and dead-letters anything else, so a bespoke type sent there would be logged
    /// as a refused message and dropped — an orphan that also looks like an error. And a new type on
    /// `orders.events` would need a new consumer on a point-to-point queue policy-service is already bound
    /// to.</para>
    ///
    /// <para>Re-emitting <c>OrderPendingApproval</c> means whatever routes a newly-gated order routes a
    /// re-gated one, with no new seam. The care timeline records "sent for approval" a second time, which is
    /// exactly what happened and exactly what a desk chasing the order needs to see. The before/after fields
    /// ride along so the reviewer is not made to re-derive the change from two screens.</para>
    /// </summary>
    public const string PendingApproval = "OrderPendingApproval";

    /// <summary>
    /// 30.4 — what approvals is told when an amendment leaves the approved scope (design 46 §5): the item, and
    /// what it was BEFORE and AFTER. A reviewer asked to re-approve something needs to see what changed; a
    /// bare "this order was amended" makes them re-derive it from two screens.
    /// </summary>
    public static object BeyondScope(
        InvestigationOrder order, OrderLine before, decimal amendedQuantity, LineAmendmentRecord record) => new
    {
        tenantId = order.TenantId,
        // The care feed mirrors OrderPendingApproval, so the encounter is mandatory: a step without it has
        // no episode, and a step on the wrong episode is worse than a missing one.
        encounterId = order.EncounterId,
        orderedByUserId = record.AmendedBy.ToString(),
        reason = "amended-beyond-approved-scope",
        authorizationId = order.AuthorizationId,
        beneficiaryId = order.BeneficiaryId,
        orderId = order.OrderId,
        order.OrderNo,
        orderLineId = before.OrderLineId,
        newLineId = record.NewLineId,
        code = before.Code,
        previousQuantity = before.QuantityOrdered,
        amendedQuantity,
        reasonCode = record.ReasonCode,
        reasonText = record.ReasonText,
        amendedByUserId = record.AmendedBy,
        amendedAt = record.AmendedAt,
    };


    /// <summary>
    /// The notification-shaped copy (design 46 §6). The publisher names the RECIPIENTS because only it knows
    /// who is waiting on this order — which is exactly why this is a second enqueue and not a second consumer
    /// on the domain stream.
    /// </summary>
    public static object Notification(
        InvestigationOrder order, OrderLine line, LineAmendmentRecord record) => new
    {
        tenantId = order.TenantId,
        entityRef = order.OrderNo,
        recipients = Recipients(order, record),
        fields = new
        {
            orderNo = order.OrderNo,
            code = line.Code,
            reasonCode = record.ReasonCode,
            amendedAt = record.AmendedAt,
        },
    };

    /// <summary>
    /// Who is told, per design 46 §6. The ordering doctor is included <b>only when somebody else made the
    /// change</b> — a confirmation of your own action is noise, and noise is what teaches people to stop
    /// reading the channel that also carries "your patient's antibiotic was withdrawn".
    /// </summary>
    private static object[] Recipients(InvestigationOrder order, LineAmendmentRecord record)
    {
        var list = new List<object>
        {
            // The fulfilling provider. Their QUEUE has already changed in the same transaction; this is the
            // additional nudge for a centre that may be preparing the work right now. Omitted entirely when
            // the order has no external owner — null means "nobody", never "everyone".
            new { role = "beneficiary", userId = order.BeneficiaryId.ToString(), locale = "ar" },
        };
        if (order.AssignedProviderId is { } provider)
            list.Add(new { role = "provider", userId = provider.ToString(), locale = "ar" });

        var orderedBy = order.CreatedBy;
        if (!string.IsNullOrWhiteSpace(orderedBy) && orderedBy != record.AmendedBy.ToString())
            list.Add(new { role = "doctor", userId = orderedBy, locale = "en" });

        return [.. list];
    }
}
