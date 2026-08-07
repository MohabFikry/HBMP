using Mersal.Events;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;

namespace Mersal.Orders.Api;

/// <summary>
/// 30.2/30.5 — the events an amendment publishes, and the reason each one exists.
///
/// <para><b>Every event here has a real subscriber, and the subscriber is named.</b> The platform already
/// carries a pile of published-and-unheard event types, and the way that pile grows is one call site at a
/// time, each looking reasonable on its own.</para>
///
/// <list type="bullet">
/// <item><c>orders.events</c> → policy-service's accumulator reads it, and the <see cref="CareFeed"/> relay
/// mirrors it to emr-service's episode timeline. The accumulator ignores these two types deliberately — a
/// cancellation consumes nothing, so there is no benefit movement to make — but the timeline records them,
/// which is the whole point: a doctor reading a visit must see that an order was withdrawn.</item>
/// <item><c>notification.domain-events</c> → notification-service, which is template-driven and needs no new
/// consumer. A SECOND, differently-shaped copy rather than a second consumer on the same queue, because the
/// transport is point-to-point: binding notification to <c>orders.events</c> would have RabbitMQ deal each
/// message to either policy or notification, and half the benefit accumulator would silently stop.</item>
/// </list>
///
/// <para><b>Why the fulfilling provider is not on this list.</b> Their queue is a live query over
/// <c>orders.order_line</c> — see <c>Queue.AvailableOrders</c> and <c>ProcedureProvider.Owned</c> — so a
/// cancelled line leaves it in the SAME TRANSACTION as the cancellation. Invariant 6 ("propagation updates
/// the fulfilling party's queue") is satisfied structurally rather than eventually, which is stronger than
/// an event could make it. The notification is additional, exactly as design 46 §6 requires.</para>
/// </summary>
public static class AmendmentEvents
{
    public const string LineCancelled = "OrderLineCancelled";
    public const string LineAmended = "OrderLineAmended";

    public static async Task PublishCancelledAsync(
        IOutbox outbox, InvestigationOrder order, OrderLine line, LineAmendmentRecord record, CancellationToken ct)
    {
        await outbox.EnqueueAsync(LineCancelled, "orders.events", Payload(order, line, record, null), ct);
        await NotifyAsync(outbox, order, line, record, LineCancelled, ct);
    }

    public static async Task PublishAmendedAsync(
        IOutbox outbox, InvestigationOrder order, OrderLine line, LineAmendmentRecord record, CancellationToken ct)
    {
        await outbox.EnqueueAsync(LineAmended, "orders.events",
            Payload(order, line, record, record.NewLineId), ct);
        await NotifyAsync(outbox, order, line, record, LineAmended, ct);
    }

    /// <summary><c>encounterId</c> is mandatory on anything the care feed mirrors: without it a step has no
    /// episode to attach to, and a step on the WRONG episode is worse than a missing one. Asserted by
    /// <c>CareFeedEnvelopeArchitectureTests</c>, which fails the build if a mirrored payload drops it.</summary>
    private static object Payload(
        InvestigationOrder order, OrderLine line, LineAmendmentRecord record, Guid? newLineId) => new
    {
        tenantId = order.TenantId,
        orderId = order.OrderId,
        order.OrderNo,
        encounterId = order.EncounterId,
        beneficiaryId = order.BeneficiaryId,
        orderLineId = line.OrderLineId,
        newLineId,
        code = line.Code,
        orderType = order.OrderType.ToString(),
        reasonCode = record.ReasonCode,
        reasonText = record.ReasonText,
        amendedByUserId = record.AmendedBy,
        amendedAt = record.AmendedAt,
        assignedProviderId = order.AssignedProviderId,
    };

    /// <summary>
    /// The notification-shaped copy (design 46 §6). The publisher names the RECIPIENTS because only it knows
    /// who is waiting on this order — which is exactly why this is a second enqueue and not a second
    /// consumer on the domain stream.
    /// </summary>
    private static async Task NotifyAsync(
        IOutbox outbox, InvestigationOrder order, OrderLine line, LineAmendmentRecord record, string eventType,
        CancellationToken ct) =>
        await outbox.EnqueueAsync(eventType, "notification.domain-events", new
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
        }, ct);

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
            // additional nudge for a centre that may be preparing the work right now.
            new { role = "provider", userId = order.AssignedProviderId?.ToString(), locale = "ar" },
            new { role = "beneficiary", userId = order.BeneficiaryId.ToString(), locale = "ar" },
        };

        var orderedBy = order.CreatedBy;
        if (!string.IsNullOrWhiteSpace(orderedBy) && orderedBy != record.AmendedBy.ToString())
            list.Add(new { role = "doctor", userId = orderedBy, locale = "en" });

        return [.. list.Where(r => r.GetType().GetProperty("userId")!.GetValue(r) is not null)];
    }
}
