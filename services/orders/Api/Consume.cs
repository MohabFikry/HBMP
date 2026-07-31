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
                            benefitCategory = BenefitCategoryMap.ForOrderType(order.OrderType),
                            serviceDate = calendar.Today(),   // 18.A3 — Cairo service date
                            // 19.4 — WHO delivered it, so policy-service can attribute the movement to a
                            // network tier resolved at the service date. Empty when the principal carries no
                            // provider; policy reports that as unattributed rather than assuming in-network.
                            providerId = provider == Guid.Empty ? (Guid?)null : provider,
                            lines = fulfillments.Select(f => new { f.OrderLineId, f.Quantity }),
                            idempotencyKey = idem,
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
