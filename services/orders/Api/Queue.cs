using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Api;

/// <summary>Phase 5.1 provider-facing fulfillment queue + search (US-040). A lab/imaging provider sees ONLY the
/// order lines it may act on — matched to its capability (Lab vs Imaging), still-available (order Active/PartiallyUsed,
/// line not used/cancelled) — projected to the minimum a fulfiller needs (patient id, line code, remaining qty),
/// never diagnoses, notes, or any pharmacy data (this service does not expose them). Every PHI read is audited.</summary>
public static class QueueEndpoints
{
    public static void MapQueue(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1/investigation-orders").RequireAuthorization();

        // ---- Queue: available work for the caller's capability ----
        v1.MapGet("/queue", async (
            HttpContext http, OrdersDbContext db, FulfillmentGate gate, IAuditClient audit, IHbmpPrincipalAccessor me,
            int page, int pageSize, CancellationToken ct) =>
        {
            var denied = await gate.AuthorizeQueueAsync(ct);
            if (denied is not null) return denied;

            var caps = ProviderCapability.ForRoles(me.Principal!.Roles).Select(t => t.ToString()).ToHashSet();
            var (p, ps) = Page(page, pageSize);

            var items = await AvailableOrders(db, caps)
                .OrderBy(o => o.RequestedAt).Skip((p - 1) * ps).Take(ps)
                .ToListAsync(ct);

            await AuditRead(audit, me, "queue", items.Count);
            return Results.Ok(items.Select(QueueItemResponse.From));
        }).RequireAuthorization(HbmpPolicies.Scope("orders:read"));

        // ---- Search by patient (beneficiary id) OR order number ----
        v1.MapGet("/search", async (
            HttpContext http, OrdersDbContext db, FulfillmentGate gate, IAuditClient audit, IHbmpPrincipalAccessor me,
            string? patientIdentifier, string? orderNo, CancellationToken ct) =>
        {
            var denied = await gate.AuthorizeQueueAsync(ct);
            if (denied is not null) return denied;

            if (string.IsNullOrWhiteSpace(patientIdentifier) && string.IsNullOrWhiteSpace(orderNo))
                return Results.Problem(statusCode: 400, title: "search requires patientIdentifier or orderNo",
                    type: "urn:hbmp:search-criteria-required");

            var caps = ProviderCapability.ForRoles(me.Principal!.Roles).Select(t => t.ToString()).ToHashSet();
            var q = AvailableOrders(db, caps);

            if (!string.IsNullOrWhiteSpace(orderNo))
                q = q.Where(o => o.OrderNo == orderNo);
            if (!string.IsNullOrWhiteSpace(patientIdentifier) && Guid.TryParse(patientIdentifier, out var ben))
                q = q.Where(o => o.BeneficiaryId == ben);
            else if (!string.IsNullOrWhiteSpace(patientIdentifier))
                return Results.Ok(Array.Empty<QueueItemResponse>());   // unknown identifier form → nothing

            var items = await q.OrderBy(o => o.RequestedAt).Take(100).ToListAsync(ct);
            await AuditRead(audit, me, "search", items.Count);
            return Results.Ok(items.Select(QueueItemResponse.From));
        }).RequireAuthorization(HbmpPolicies.Scope("orders:read"));
    }

    /// <summary>Orders the caller may fulfil: type ∈ their capability, order still open, with ≥1 available line.
    /// The projection to available lines happens in <see cref="QueueItemResponse.From"/>.</summary>
    private static IQueryable<InvestigationOrder> AvailableOrders(OrdersDbContext db, HashSet<string> capabilities) =>
        db.Orders.AsNoTracking().Include(o => o.Lines)
            .Where(o => capabilities.Contains(o.OrderType.ToString()))
            .Where(o => o.Status == OrderStatus.Active || o.Status == OrderStatus.PartiallyUsed)
            .Where(o => o.Lines.Any(l => l.Status == OrderLineStatus.Active || l.Status == OrderLineStatus.PartiallyUsed));

    private static (int page, int pageSize) Page(int page, int pageSize) =>
        (page < 1 ? 1 : page, pageSize is < 1 or > 100 ? 25 : pageSize);

    private static async Task AuditRead(IAuditClient audit, IHbmpPrincipalAccessor me, string op, int count) =>
        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "investigation_order", EntityId = op, Action = AuditAction.Read,
            ActorUserId = me.Principal?.Subject, DecisionOutcome = "Allow",
            DecisionReasonCode = $"provider-{op}:{count}", FieldClasses = ["phi"],
        });
}
