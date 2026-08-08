using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Mersal.BeneficiaryLookup;

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
            // NULLABLE, and that is the fix for a real defect: these were non-nullable `int`, so a caller
            // hitting GET /queue with no query string — which is the natural call, and what the bench screen
            // makes — got a 500 from the model binder instead of the first page. The Page() helper below has
            // always clamped and defaulted them; nothing ever let it, because the request never got that far.
            // The procedure-centre queue (ProcedureProvider.cs) has always used the nullable form.
            TimeProvider clock, int? page, int? pageSize, CancellationToken ct) =>
        {
            var denied = await gate.AuthorizeQueueAsync(ct);
            if (denied is not null) return denied;

            var caps = ProviderCapability.ForRoles(me.Principal!.Roles).ToHashSet();
            var (p, ps) = Page(page ?? 1, pageSize ?? 25);

            var items = await AvailableOrders(db, caps)
                .OrderBy(o => o.RequestedAt).Skip((p - 1) * ps).Take(ps)
                .ToListAsync(ct);

            await AuditRead(audit, me, "queue", items.Count);
            var now = clock.GetUtcNow();
            return Results.Ok(items.Select(o => QueueItemResponse.From(o, now)));
        }).RequireAuthorization(HbmpPolicies.Scope("orders:read"));

        // ---- Search by patient (beneficiary id) OR order number ----
        v1.MapGet("/search", async (
            HttpContext http, OrdersDbContext db, FulfillmentGate gate, IAuditClient audit, IHbmpPrincipalAccessor me,
            IBeneficiaryResolver resolver, TimeProvider clock,
            string? patientIdentifier, string? orderNo,
            string? cardNumber, string? passport, string? memberNo,
            CancellationToken ct) =>
        {
            var denied = await gate.AuthorizeQueueAsync(ct);
            if (denied is not null) return denied;

            if (string.IsNullOrWhiteSpace(patientIdentifier) && string.IsNullOrWhiteSpace(orderNo)
                && string.IsNullOrWhiteSpace(cardNumber) && string.IsNullOrWhiteSpace(passport)
                && string.IsNullOrWhiteSpace(memberNo))
            {
                return Results.Problem(
                    statusCode: 400,
                    title: "search requires orderNo, patientIdentifier, cardNumber, passport or memberNo",
                    type: "urn:hbmp:search-criteria-required");
            }

            var caps = ProviderCapability.ForRoles(me.Principal!.Roles).ToHashSet();
            var q = AvailableOrders(db, caps);

            if (!string.IsNullOrWhiteSpace(orderNo))
                q = q.Where(o => o.OrderNo == orderNo);
            else if (!string.IsNullOrWhiteSpace(patientIdentifier) && Guid.TryParse(patientIdentifier, out var ben))
                q = q.Where(o => o.BeneficiaryId == ben);
            else if (!string.IsNullOrWhiteSpace(patientIdentifier))
                return Results.Ok(Array.Empty<QueueItemResponse>());   // unknown identifier form → nothing
            else
            {
                // card / passport / member number → resolve to a beneficiary via patient-service, through the
                // SAME shared lookup the dispensing counter uses. TWO identifiers are required (doc 43 §7 D5):
                // a card number is printed on something that gets shared, photographed and reused, so one
                // number must not open a person's record.
                //
                // Each outcome answers a different question, and only ONE of them is "no orders". Returning an
                // empty list for all of them is the defect the pharmacy counter already had and fixed: a
                // technician whose token could not read the directory would be told a patient with three live
                // orders had none — a 200 carrying a wrong answer, which invites no second look.
                var resolution = await resolver.ResolveAsync(
                    cardNumber, passport, memberNo, http.Request.Headers.Authorization.ToString(), ct);

                switch (resolution.Outcome)
                {
                    case ResolveOutcome.TooFewIdentifiers:
                        return Results.Problem(
                            statusCode: 422, title: "two-identifiers-required",
                            type: "urn:hbmp:two-identifiers-required",
                            detail: "Searching by card number, passport or member number requires at least two "
                                    + "of them. A card number alone is a lookup key, not proof of identity.");
                    case ResolveOutcome.Unavailable:
                        return Results.Problem(
                            statusCode: 503, title: "patient-directory-unavailable",
                            type: "urn:hbmp:patient-directory-unavailable",
                            detail: "The patient directory could not be reached, so these identifiers could not "
                                    + "be resolved. This is NOT a report that the patient has no orders.");
                    case ResolveOutcome.NotFound:
                        // A real answer: those identifiers match nobody. An empty list is correct here.
                        return Results.Ok(Array.Empty<QueueItemResponse>());
                    default:
                        q = q.Where(o => o.BeneficiaryId == resolution.BeneficiaryId!.Value);
                        break;
                }
            }

            var items = await q.OrderBy(o => o.RequestedAt).Take(100).ToListAsync(ct);
            await AuditRead(audit, me, "search", items.Count);
            var searchNow = clock.GetUtcNow();
            return Results.Ok(items.Select(o => QueueItemResponse.From(o, searchNow)));
        }).RequireAuthorization(HbmpPolicies.Scope("orders:read"));

        // ---- Awaiting result: lines THIS provider has consumed but not yet uploaded a result for (US-042). ----
        // Drives the result-upload worklist; a result may only be attached to a line this provider consumed.
        v1.MapGet("/awaiting-result", async (
            OrdersDbContext db, FulfillmentGate gate, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var denied = await gate.AuthorizeQueueAsync(ct);
            if (denied is not null) return denied;
            var provider = Guid.TryParse(me.Principal?.ProviderId, out var pg) ? pg : Guid.Empty;

            var rows = await (
                from f in db.Fulfillments.AsNoTracking()
                where f.PerformingProviderId == provider && f.ResultUploadedAt == null
                join l in db.Set<OrderLine>().AsNoTracking() on f.OrderLineId equals l.OrderLineId
                join o in db.Orders.AsNoTracking() on l.OrderId equals o.OrderId
                orderby f.ConsumedAt descending
                select new AwaitingResultResponse(
                    o.OrderId, l.OrderLineId, o.OrderNo, o.OrderType.ToString(), o.BeneficiaryId,
                    l.Code, l.Description, f.ConsumedAt)
            ).Take(100).ToListAsync(ct);

            await AuditRead(audit, me, "awaiting-result", rows.Count);
            return Results.Ok(rows);
        }).RequireAuthorization(HbmpPolicies.Scope("orders:read"));
    }

    /// <summary>A consumed line still awaiting its result upload (US-042) — the provider's result worklist row.</summary>
    public sealed record AwaitingResultResponse(
        Guid OrderId, Guid LineId, string OrderNo, string OrderType, Guid BeneficiaryId,
        string Code, string? Description, DateTimeOffset ConsumedAt);

    /// <summary>Orders the caller may fulfil: type ∈ their capability, order still open, with ≥1 available line.
    /// The projection to available lines happens in <see cref="QueueItemResponse.From"/>.</summary>
    private static IQueryable<InvestigationOrder> AvailableOrders(OrdersDbContext db, HashSet<OrderType> capabilities) =>
        db.Orders.AsNoTracking().Include(o => o.Lines)
            .Where(o => capabilities.Contains(o.OrderType))
            // EXPIRED IS INCLUDED. Dropping it meant a technician with the patient in front of them saw an
            // empty queue and had nothing to tell them — the same defect the dispensing search had, where a
            // true statement ("nothing fulfillable") stood in for a false one ("nothing"). Expired orders are
            // returned, flagged, and still refused by the consume rule; the recovery is an extension request.
            .Where(o => o.Status == OrderStatus.Active || o.Status == OrderStatus.PartiallyUsed
                        || o.Status == OrderStatus.Expired)
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
