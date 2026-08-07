using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Events;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Mersal.Time;

namespace Mersal.Orders.Api;

/// <summary>Phase 5.2 — the ATOMIC, IDEMPOTENT, DUPLICATE-PROOF consume (US-041); the single most important endpoint
/// in the platform. The three-mechanism guard (unique idempotency key + line <c>xmin</c> optimistic concurrency +
/// required <c>Idempotency-Key</c>) lives in <see cref="ConsumeExecutor"/> so it is exercised identically by the
/// concurrency tests. Here we add the edges: capability/provider-ownership authorization, problem+json mapping,
/// the OrderLinesConsumed/OrderCompleted outbox events (atomic with the state change) and the audit trail.</summary>
public static class ConsumeEndpoints
{
    public static void MapConsume(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1/investigation-orders").RequireAuthorization();

        v1.MapPost("/{orderId:guid}/consume", async (
            Guid orderId, ConsumeRequest req, HttpRequest http, OrdersDbContext db, ConsumeExecutor executor,
            FulfillmentGate gate, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar,
            CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required", type: "urn:hbmp:idempotency-required");
            if (req.Lines is null || req.Lines.Count == 0)
                return Results.Problem(statusCode: 400, title: "consume requires at least one line", type: "urn:hbmp:empty-consume");

            var head = await db.Orders.AsNoTracking().Where(o => o.OrderId == orderId)
                .Select(o => new { o.OrderType }).FirstOrDefaultAsync(ct);
            if (head is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            // Provider-ownership + Lab/Imaging capability (audited 403 on refusal).
            var denied = await gate.AuthorizeConsumeAsync(head.OrderType, ct);
            if (denied is not null) return denied;

            var provider = Guid.TryParse(me.Principal?.ProviderId, out var pg) ? pg : Guid.Empty;
            var actor = Guid.TryParse(me.Principal?.Subject, out var ag) ? ag : Guid.Empty;
            var reqs = req.Lines.Select(l => new ConsumeLineRequest(l.OrderLineId, l.Quantity)).ToList();

            var result = await executor.ConsumeAsync(orderId, idem, provider, actor, reqs, clock.GetUtcNow(),
                insideTransaction: async (order, fulfillments, c) =>
                {
                    // 18.A1: the payload now carries the tenant, the beneficiary, the benefit category and
                    // the service date — everything policy-service needs to move the coverage accumulator
                    // (FR-INV-006). Additive fields only; existing consumers (claims intake) are unaffected.
                    await outbox.EnqueueAsync("OrderLinesConsumed", "orders.events",
                        new
                        {
                            orderId,
                            // `orderType` — the read model splits utilization into Lab and Radiology by it, and
                            // uses the benefit category as the code. Neither was on the wire, so every
                            // consumed line would have landed in the Lab bucket under "unknown".
                            orderType = order.OrderType.ToString(),
                            tenantId = order.TenantId,
                            beneficiaryId = order.BeneficiaryId,
                            // ADR-0031 — the episode this fulfilment belongs to, plus the business key the
                            // step is referenced by. Without the encounter a "sample taken" step has no visit
                            // to hang from; without the number it has nothing a human can look up.
                            encounterId = order.EncounterId,
                            order.OrderNo,
                            benefitCategory = BenefitCategoryMap.ForOrderType(order.OrderType),
                            serviceDate = calendar.Today(),   // 18.A3 — Cairo service date
                            // 19.4 — WHO delivered it, so policy-service can attribute the movement to a
                            // network tier resolved at the service date. Empty when the principal carries no
                            // provider; policy reports that as unattributed rather than assuming in-network.
                            providerId = provider == Guid.Empty ? (Guid?)null : provider,
                            lines = fulfillments.Select(f => new { f.OrderLineId, f.Quantity }),
                            idempotencyKey = idem,
                        }, c);
                    /*
                     * ADR-0034 — the SECOND, APPROVALS-SHAPED COPY, exactly as the dispensing counter sends.
                     * Performing a panel is an authorized act separate from the order that asked for it, and
                     * this is what makes it one.
                     *
                     * Its own queue, not `orders.events`: that transport is point-to-point and policy-service
                     * already consumes it to move the benefit accumulator, so a second consumer there would
                     * compete for messages and the accumulator would silently stop advancing.
                     *
                     * `orderedCode` and `fulfilledCode` are the same here, and the field pair is still
                     * carried: there is no substitution on this path (an investigation has no equivalence set
                     * in master data, so a technician's alternative is a REQUEST to the approval team, not a
                     * choice), and a schema that could only express "same" would have to change the day one
                     * is approved.
                     */
                    await outbox.EnqueueAsync("FulfilmentRecorded", "approvals.fulfilments",
                        new
                        {
                            tenantId = order.TenantId,
                            beneficiaryId = order.BeneficiaryId,
                            providerId = provider == Guid.Empty ? (Guid?)null : provider,
                            encounterId = order.EncounterId,
                            source = "OrderLine",
                            sourceRef = orderId.ToString(),
                            sourceNo = order.OrderNo,
                            benefitCategory = BenefitCategoryMap.ForOrderType(order.OrderType),
                            actorUserId = me.Principal?.Subject,
                            fulfilledAt = fulfillments.Count == 0 ? clock.GetUtcNow() : fulfillments[0].ConsumedAt,
                            items = fulfillments.Select(f =>
                            {
                                var line = order.Lines.FirstOrDefault(l => l.OrderLineId == f.OrderLineId);
                                return new
                                {
                                    fulfilmentRef = f.FulfillmentId.ToString(),
                                    sourceLineId = f.OrderLineId,
                                    orderedCode = line?.Code ?? f.OrderLineId.ToString(),
                                    orderedLabel = line?.Description,
                                    fulfilledCode = line?.Code ?? f.OrderLineId.ToString(),
                                    fulfilledLabel = line?.Description,
                                    quantity = f.Quantity,
                                    substitutionReason = (string?)null,
                                };
                            }),
                        }, c);

                    if (order.Status == OrderStatus.Completed)
                        await outbox.EnqueueAsync("OrderCompleted", "orders.events", new { tenantId = order.TenantId, orderId, order.OrderNo }, c);
                }, ct);

            switch (result.Outcome)
            {
                case ConsumeOutcome.Applied:
                    foreach (var f in result.Fulfillments)
                        await audit.EmitAsync(new AuditEventDraft
                        {
                            EntityType = "order_fulfillment", EntityId = f.FulfillmentId.ToString(), Action = AuditAction.StateChange,
                            ActorUserId = me.Principal?.Subject, DecisionOutcome = "Consumed",
                            DecisionReasonCode = $"order:{orderId};line:{f.OrderLineId};qty:{f.Quantity};key:{idem}",
                        }, ct);
                    return Results.Ok(ConsumeResponse.From(result.Order!, result.Fulfillments, replayed: false));

                case ConsumeOutcome.Replayed:
                    return Results.Ok(ConsumeResponse.From(result.Order!, result.Fulfillments, replayed: true));

                case ConsumeOutcome.NotFound:
                    return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
                case ConsumeOutcome.LineNotFound:
                    return Results.Problem(statusCode: 404, title: "line-not-found", type: "urn:hbmp:line-not-found",
                        detail: "No such order line on this order.");
                case ConsumeOutcome.InvalidQuantity:
                    return Results.Problem(statusCode: 400, title: "invalid-quantity", type: "urn:hbmp:invalid-quantity",
                        detail: "Consume quantity must be greater than zero.");
                case ConsumeOutcome.AlreadyUsed:
                    return Results.Problem(statusCode: 409, title: "already-used", type: "urn:hbmp:line-already-used",
                        detail: "This line has already been used and cannot be consumed again.");
                case ConsumeOutcome.OverConsume:
                    return Results.Problem(statusCode: 422, title: "over-consume", type: "urn:hbmp:over-consume",
                        detail: "Requested quantity exceeds the remaining quantity on the line.");
                case ConsumeOutcome.OrderNotConsumable:
                    return Results.Problem(statusCode: 409, title: "order-not-consumable", type: "urn:hbmp:order-not-consumable",
                        detail: "This order is not in a consumable state.");
                // Its own problem type, because the recovery is specific and the technician has the patient
                // in front of them: an expired order can be revalidated by the approval team, whereas one
                // that is cancelled or complete is finished. "Not in a consumable state" says neither.
                case ConsumeOutcome.OrderExpired:
                    return Results.Problem(statusCode: 409, title: "order-expired", type: "urn:hbmp:order-expired",
                        detail: "This order is past its validity window and cannot be fulfilled. The approval "
                                + "team can revalidate it — the patient does not need a new order from a doctor.");
                case ConsumeOutcome.InvalidIdempotencyKey:
                    return Results.Problem(statusCode: 400, title: "invalid-idempotency-key", type: "urn:hbmp:invalid-idempotency-key",
                        detail: "The Idempotency-Key must be non-empty, at most 80 characters, and must not contain '::'.");
                case ConsumeOutcome.IdempotencyKeyReuse:
                    return Results.Problem(statusCode: 422, title: "idempotency-key-reuse", type: "urn:hbmp:idempotency-key-reuse",
                        detail: "This Idempotency-Key was already used for a different request. Use a new key for a changed request.");
                case ConsumeOutcome.Conflict:
                    return Results.Problem(statusCode: 409, title: "concurrent-consume", type: "urn:hbmp:concurrent-consume",
                        detail: "The line was concurrently consumed by another request; re-read and retry.");
                default:
                    return Results.Problem(statusCode: 400, title: "invalid-consume");
            }
        }).RequireAuthorization(HbmpPolicies.Scope("orders:consume"));
    }
}
