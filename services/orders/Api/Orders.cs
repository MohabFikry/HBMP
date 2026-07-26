using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Api;

/// <summary>Phase 4.2 investigation-order endpoints: create (treating-gated, code-validated, routed to approval
/// or auto-activated) and read/cancel. Order + lines + state change are written in one transaction and the
/// domain events (OrderCreated, then OrderActivated | OrderPendingApproval) are enqueued to the outbox; consumers
/// dedupe on event id. Every mutation is audited.</summary>
public static class OrdersEndpoints
{
    public static void MapOrders(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1/investigation-orders").RequireAuthorization();

        // ---- Create (US-032) ----
        v1.MapPost("", async (
            CreateOrderRequest req, HttpRequest http, OrdersDbContext db, OrdersGate gate, ICodeValidator codes,
            IExaminationTypeResolver examTypes, OrderRoutingOptions routing, OrderNoIssuer orderNos, IAuditClient audit,
            IOutbox outbox, IHbmpPrincipalAccessor me, BranchScopeState branch, TimeProvider clock, CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required", type: "urn:hbmp:idempotency-required");

            // Idempotent replay → return the existing order.
            var existing = await db.Orders.AsNoTracking().Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.IdempotencyKey == idem, ct);
            if (existing is not null) return Results.Ok(OrderResponse.From(existing));

            if (req.Lines is null || req.Lines.Count == 0)
                return Results.Problem(statusCode: 400, title: "an order must have at least one line", type: "urn:hbmp:empty-order");

            var bearer = http.Headers.Authorization.ToString();

            // Treating-relationship gate (403 + audit if denied).
            var denied = await gate.CheckAsync(OrdersPolicies.Create, null, req.BeneficiaryId, bearer, ct);
            if (denied is not null) return denied;

            // Validate every line code against masterdata (unknown → 422 problem+json). Fail-closed.
            foreach (var line in req.Lines)
            {
                if (line.QuantityOrdered <= 0)
                    return Results.Problem(statusCode: 422, title: "invalid-quantity", type: "urn:hbmp:invalid-quantity",
                        detail: $"Line '{line.Code}' must have quantityOrdered > 0.");
                if (!await codes.IsValidAsync(line.CodeSystem, line.Code, bearer, ct))
                    return Results.Problem(statusCode: 422, title: "unknown-code", type: "urn:hbmp:unknown-code",
                        detail: $"{line.CodeSystem} code '{line.Code}' is not present in master data.");
            }

            // 14.6 — resolve + PIN examination-type sensitivity (fail-closed: unknown → 422).
            var classifications = new Dictionary<Guid, ExaminationClassification>();
            foreach (var et in req.Lines.Where(l => l.ExaminationTypeId is not null).Select(l => l.ExaminationTypeId!.Value).Distinct())
            {
                var cls = await examTypes.ResolveAsync(et, bearer, ct);
                if (cls is null)
                    return Results.Problem(statusCode: 422, title: "unknown-examination-type", type: "urn:hbmp:unknown-examination-type",
                        detail: $"examination type '{et}' is not present in master data.");
                classifications[et] = cls;
            }

            var now = clock.GetUtcNow();
            var actor = me.Principal?.Subject;
            var providerId = Guid.TryParse(me.Principal?.ProviderId, out var pg) ? pg : Guid.Empty;

            var order = new InvestigationOrder
            {
                OrderId = Guid.NewGuid(), OrderNo = await orderNos.NextAsync(now.Year, ct),
                BeneficiaryId = req.BeneficiaryId, EncounterId = req.EncounterId, OrderingProviderId = providerId,
                OrderingBranchId = branch.Context.ActiveBranchId,   // phase 14.4 — pin the raising branch
                OrderType = req.OrderType, Status = OrderStatus.Requested, RequestedAt = now, ExpiresAt = req.ExpiresAt,
                IdempotencyKey = idem, CreatedBy = actor,
                Lines = req.Lines.Select(l => new OrderLine
                {
                    OrderLineId = Guid.NewGuid(), CodeSystem = l.CodeSystem, Code = l.Code,
                    Description = l.Description, QuantityOrdered = l.QuantityOrdered, Status = OrderLineStatus.Active,
                    ExaminationTypeId = l.ExaminationTypeId,
                    SensitivityLevel = l.ExaminationTypeId is { } etId ? classifications[etId].SensitivityLevel : SensitivityLevel.Standard,
                }).ToList(),
            };
            // Order sensitivity = the strictest of its lines (14.6).
            order.SensitivityLevel = order.Lines.Select(x => x.SensitivityLevel).DefaultIfEmpty(SensitivityLevel.Standard).Max();

            // Route: gated → PendingApproval; else auto-activate.
            var route = OrderRoutingPolicy.Evaluate(order, routing);
            order.Status = route.RouteToApproval ? OrderStatus.PendingApproval : OrderStatus.Active;

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);

            // Outbox events in the same transaction as the state change (consumers dedupe on event id).
            await outbox.EnqueueAsync("OrderCreated", "orders.events",
                new { orderId = order.OrderId, order.OrderNo, beneficiaryId = order.BeneficiaryId, orderType = order.OrderType.ToString() }, ct);
            if (route.RouteToApproval)
                await outbox.EnqueueAsync("OrderPendingApproval", "orders.events",
                    new { orderId = order.OrderId, order.OrderNo, reason = route.Reason }, ct);
            else
                await outbox.EnqueueAsync("OrderActivated", "orders.events",
                    new { orderId = order.OrderId, order.OrderNo }, ct);
            await tx.CommitAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "investigation_order", EntityId = order.OrderId.ToString(), Action = AuditAction.Create,
                ActorUserId = actor, DecisionOutcome = order.Status.ToString(), DecisionReasonCode = route.Reason,
                AfterState = $"{{\"orderNo\":\"{order.OrderNo}\",\"status\":\"{order.Status}\"}}",
            }, ct);

            return Results.Created($"/api/v1/investigation-orders/{order.OrderId}", OrderResponse.From(order));
        }).RequireAuthorization(HbmpPolicies.Scope("orders:write"));

        // ---- Read (treating clinician) ----
        v1.MapGet("/{id:guid}", async (Guid id, HttpRequest http, OrdersDbContext db, OrdersGate gate, CancellationToken ct) =>
        {
            var order = await db.Orders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.OrderId == id, ct);
            if (order is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            var denied = await gate.CheckAsync(OrdersPolicies.Read, id.ToString(), order.BeneficiaryId, http.Headers.Authorization.ToString(), ct);
            if (denied is not null) return denied;
            return Results.Ok(OrderResponse.From(order));
        });

        // ---- My orders (ordering clinician's worklist, US-032) ----
        // The orders I created, newest first, optionally filtered by status (e.g. Completed = the results inbox).
        // Scoped to the caller by CreatedBy == subject — no cross-clinician leakage, no treating-gate needed
        // (you always have a relationship with an order you authored).
        v1.MapGet("/mine", async (string? status, OrdersDbContext db, IHbmpPrincipalAccessor me, BranchScopeState branch, CancellationToken ct) =>
        {
            var sub = me.Principal?.Subject;
            if (string.IsNullOrWhiteSpace(sub)) return Results.Ok(Array.Empty<OrderResponse>());
            var q = db.Orders.AsNoTracking().Include(o => o.Lines).Where(o => o.CreatedBy == sub);
            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var st))
                q = q.Where(o => o.Status == st);
            // 14.4 — a BranchScoped clinician sees only orders raised in the active branch.
            if (branch.Context.ActiveBranchId is { } active) q = q.Where(o => o.OrderingBranchId == active);
            var rows = await q.OrderByDescending(o => o.RequestedAt).Take(100).ToListAsync(ct);
            return Results.Ok(rows.Select(OrderResponse.From));
        }).RequireAuthorization(HbmpPolicies.Scope("orders:read"));

        // ---- Cancel (not yet fully consumed) ----
        v1.MapPost("/{id:guid}/cancel", async (
            Guid id, CancelOrderRequest req, HttpRequest http, OrdersDbContext db, OrdersGate gate,
            IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.OrderId == id, ct);
            if (order is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            var denied = await gate.CheckAsync(OrdersPolicies.Create, id.ToString(), order.BeneficiaryId, http.Headers.Authorization.ToString(), ct);
            if (denied is not null) return denied;

            if (!OrderWorkflow.CanCancel(order.Status))
                return Results.Problem(statusCode: 409, title: "transition-denied", type: "urn:hbmp:transition-denied",
                    detail: $"An order in status {order.Status} cannot be cancelled.");

            order.Status = OrderStatus.Cancelled;
            foreach (var l in order.Lines.Where(l => l.Status == OrderLineStatus.Active)) l.Status = OrderLineStatus.Cancelled;
            await db.SaveChangesAsync(ct);
            await outbox.EnqueueAsync("OrderCancelled", "orders.events", new { orderId = order.OrderId, reason = req.Reason }, ct);
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "investigation_order", EntityId = order.OrderId.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "Cancelled", DecisionReasonCode = req.Reason,
            }, ct);
            return Results.Ok(OrderResponse.From(order));
        }).RequireAuthorization(HbmpPolicies.Scope("orders:write"));
    }
}
