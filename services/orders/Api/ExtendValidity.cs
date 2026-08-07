using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Mersal.Validity;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Api;

/// <summary>
/// Puts an expired investigation order back in date, on the authority of an approved authorization.
/// </summary>
/// <remarks>
/// The twin of <c>ExtendValidityEndpoints</c> in pharmacy-service — same caller (approvals, forwarding the
/// reviewer's token), same gate (<c>auth:decide</c>: only someone who may decide an authorization may move an
/// expiry), same fixed reset (the tenant's configured period counted from the DECISION, per order type), and
/// the same idempotency on the authorization id.
/// </remarks>
public static class ExtendValidityEndpoints
{
    public static void MapExtendValidity(this WebApplication app)
    {
        app.MapPost("/api/v1/investigation-orders/{id:guid}/extend-validity", async (
            Guid id, ExtendValidityRequest req, HttpRequest http, OrdersDbContext db,
            IValidityPolicySource validity, IAuditClient audit, IOutbox outbox,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var order = await db.Orders.FirstOrDefaultAsync(o => o.OrderId == id, ct);
            if (order is null) return Results.Problem(statusCode: 404, title: "Not Found",
                type: "https://mersal.foundation/problems/not-found");

            var now = clock.GetUtcNow();

            if (order.ValidityExtendedBy == req.AuthorizationId)
                return Results.Ok(new { orderId = order.OrderId, order.OrderNo, expiresAt = order.ExpiresAt, replayed = true });

            // Cancelled / Rejected / Completed orders are finished, not out of date. Revalidating one would
            // resurrect something that stopped for a reason this endpoint cannot see.
            if (order.Status is OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Completed)
                return Results.Problem(statusCode: 409, title: "not-extendable", type: "urn:hbmp:not-extendable",
                    detail: $"An order in status {order.Status} cannot be revalidated — it did not stop being "
                            + "actionable because of its date.");

            var artefact = order.OrderType switch
            {
                OrderType.Lab => ValidityArtefact.LabOrder,
                OrderType.Imaging => ValidityArtefact.ImagingOrder,
                _ => ValidityArtefact.ProcedureOrder,
            };

            var previous = order.ExpiresAt;
            var newExpiry = ValidityPolicy.ExpiryFor(now, await validity.DaysAsync(artefact, http.Headers.Authorization.ToString(), ct));

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            order.ExpiresAt = newExpiry;
            order.ValidityExtendedBy = req.AuthorizationId;
            order.ValidityExtendedAt = now;
            // Back to Active from Expired; a PartiallyUsed order keeps its status and its consumed lines.
            if (order.Status == OrderStatus.Expired) order.Status = OrderStatus.Active;

            await outbox.EnqueueAsync("OrderValidityExtended", "orders.events", new
            {
                tenantId = order.TenantId, orderId = order.OrderId, order.OrderNo, order.BeneficiaryId,
                orderType = order.OrderType.ToString(),
                authorizationId = req.AuthorizationId, req.AuthNo, previousExpiry = previous, newExpiry,
            }, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "investigation_order", EntityId = order.OrderId.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, TenantId = order.TenantId,
                BeforeState = $"{{\"expiresAt\":\"{previous:O}\"}}",
                AfterState = $"{{\"expiresAt\":\"{newExpiry:O}\",\"authorizationId\":\"{req.AuthorizationId}\"}}",
                DecisionOutcome = "ValidityExtended", DecisionReasonCode = req.AuthNo,
                Purpose = "validity-extension", Severity = AuditSeverity.Notice,
            }, ct);

            return Results.Ok(new { orderId = order.OrderId, order.OrderNo, expiresAt = newExpiry, replayed = false });
        }).RequireAuthorization(HbmpPolicies.Scope("auth:decide"));
    }
}

public sealed record ExtendValidityRequest(Guid AuthorizationId, string? AuthNo);
