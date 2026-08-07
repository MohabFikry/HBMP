using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.BeneficiaryLookup;
using Mersal.Events;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Api;

/// <summary>
/// 29.2b — the EXTERNAL delivering provider's portal (design 45 §2b): physiotherapy centres, dialysis units
/// and outside specialist clinics see the orders routed to them, verify the person at the counter, record
/// sessions one at a time, and close the loop with a report.
///
/// <para><b>Every row is scoped by <c>assigned_provider_id</c>, from the first commit.</b> Not by the caller's
/// own provider id (audit R3's <c>DispensingGate</c> defect), and not by a filter added to each query — by a
/// single <c>Owned()</c> helper that every read goes through, because a filter that can be forgotten in one
/// query is a filter that will be.</para>
///
/// <para><b>Sessions reuse <see cref="ConsumeExecutor"/> unchanged.</b> Design 45 §2b: "Reuse the consume path
/// and its concurrency proofs; do not write a second one." A session is one unit of the line's quantity, and
/// the atomic/idempotent/no-reuse guarantees that took several phases to get right apply to it untouched.</para>
/// </summary>
public static class ProcedureProviderEndpoints
{
    public static void MapProcedureProvider(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1/procedure-orders").RequireAuthorization();

        // ---- The centre's queue: its OWN routed work, and nothing else -------------------------------------
        v1.MapGet("/queue", async (
            OrdersDbContext db, ProcedureProviderGate gate, IAuditClient audit, IHbmpPrincipalAccessor me,
            TimeProvider clock, int? page, int? pageSize, CancellationToken ct) =>
        {
            if (gate.AuthorizePortal() is { } denied) return denied;

            var (p, ps) = (page is null or < 1 ? 1 : page.Value, pageSize is null or < 1 or > 100 ? 25 : pageSize.Value);
            var orders = await Owned(db, gate.CallerProviderId)
                // EXPIRED IS INCLUDED, flagged rather than hidden — the same decision the lab bench queue
                // records. A centre with the beneficiary standing in front of them seeing an empty queue has
                // nothing to tell them, and "nothing deliverable" is a different statement from "nothing".
                .Where(o => o.Status == OrderStatus.Active || o.Status == OrderStatus.PartiallyUsed
                            || o.Status == OrderStatus.Expired)
                .Where(o => o.Lines.Any(l => l.Status == OrderLineStatus.Active || l.Status == OrderLineStatus.PartiallyUsed))
                .OrderBy(o => o.RequestedAt).Skip((p - 1) * ps).Take(ps)
                .ToListAsync(ct);

            // THE QUEUE CARRIES NO NAME AND NO PHOTO. It is a list of WORK, and a centre browsing a list of
            // refugees' names is a disclosure nobody asked for — the identity check belongs at the counter,
            // with the person present, behind two identifiers (`/search` below). Minimum-necessary is not only
            // about which fields a role may see; it is about which of them this SCREEN needs.
            var now = clock.GetUtcNow();
            var items = orders
                .Select(o => (Order: o, Line: o.Lines.FirstOrDefault(
                    l => l.Status is OrderLineStatus.Active or OrderLineStatus.PartiallyUsed)))
                .Where(x => x.Line is not null)
                .Select(x => ProcedureQueueItem.From(x.Order, x.Line!, displayName: null, photoUrl: null, now))
                .ToList();

            await AuditRead(audit, me, "procedure-queue", items.Count);
            return Results.Ok(items);
        }).RequireAuthorization(HbmpPolicies.Scope("procedure:read"));

        // ---- Identity at the counter: TWO identifiers, audited ---------------------------------------------
        //
        // Design 45 §2b: "That reuses the card-number path from phase 26 Gate 6 — a SECOND identifier
        // required, minimum-necessary view, audited retrieval. A card is shared and photographed; it is not an
        // authenticator." Reused verbatim through IBeneficiaryResolver rather than reimplemented: the failure
        // paths are the ones that matter, and a second implementation drifts on exactly the case nobody tests.
        v1.MapGet("/search", async (
            OrdersDbContext db, ProcedureProviderGate gate, IAuditClient audit, IHbmpPrincipalAccessor me,
            IBeneficiaryResolver resolver, HttpRequest http, TimeProvider clock,
            string? cardNumber, string? passport, string? memberNo, CancellationToken ct) =>
        {
            if (gate.AuthorizePortal() is { } denied) return denied;

            var resolution = await resolver.ResolveAsync(
                cardNumber, passport, memberNo, http.Headers.Authorization.ToString(), ct);

            // FOUR outcomes, and only one of them means "this person has nothing". Collapsing them would tell
            // a centre whose token could not read the directory that a beneficiary with six approved sessions
            // had none — a 200 carrying a wrong answer, which is worse than an error because nothing about it
            // invites a second look.
            switch (resolution.Outcome)
            {
                case ResolveOutcome.TooFewIdentifiers:
                    return Results.Problem(statusCode: 422, title: "second-identifier-required",
                        type: "urn:hbmp:second-identifier-required",
                        detail: "Two identifiers are required. A card number is a lookup key, not proof of "
                              + "identity — cards are shared and photographed.");
                case ResolveOutcome.Unavailable:
                    return Results.Problem(statusCode: 503, title: "directory-unavailable",
                        type: "urn:hbmp:directory-unavailable",
                        detail: "The beneficiary directory could not be reached. This is NOT a report that "
                              + "the person has no orders.");
                case ResolveOutcome.NotFound:
                    return Results.Ok(Array.Empty<ProcedureQueueItem>());
            }

            var now = clock.GetUtcNow();
            var orders = await Owned(db, gate.CallerProviderId)
                .Where(o => o.BeneficiaryId == resolution.BeneficiaryId!.Value)
                .OrderBy(o => o.RequestedAt).ToListAsync(ct);

            var items = orders
                .Select(o => (Order: o, Line: o.Lines.FirstOrDefault(
                    l => l.Status is OrderLineStatus.Active or OrderLineStatus.PartiallyUsed)))
                .Where(x => x.Line is not null)
                .Select(x => ProcedureQueueItem.From(x.Order, x.Line!, displayName: null, photoUrl: null, now))
                .ToList();

            // The retrieval is audited whether or not it found anything: the question "who asked about this
            // person" is the one an investigation starts from.
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "beneficiary", EntityId = resolution.BeneficiaryId!.Value.ToString(),
                Action = AuditAction.Read, ActorUserId = me.Principal?.Subject,
                DecisionOutcome = "Allow", DecisionReasonCode = $"procedure-counter-search:{items.Count}",
                FieldClasses = ["phi"],
            }, ct);

            return Results.Ok(items);
        }).RequireAuthorization(HbmpPolicies.Scope("procedure:read"));

        // ---- Record ONE delivered session ------------------------------------------------------------------
        v1.MapPost("/{orderId:guid}/sessions", async (
            Guid orderId, RecordSessionRequest req, HttpRequest http, OrdersDbContext db,
            ProcedureProviderGate gate, ConsumeExecutor executor, IAuditClient audit, IOutbox outbox,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required",
                    type: "urn:hbmp:idempotency-required",
                    detail: "Each session carries its own key: a double-tapped 'record session' must not burn "
                          + "two of a beneficiary's approved visits.");

            // Ownership is checked against the ROW before anything else happens.
            var order = await Owned(db, gate.CallerProviderId).FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
            if (gate.AuthorizeOrder(order) is { } denied) return denied;

            var line = order!.Lines.FirstOrDefault(l => l.OrderLineId == req.OrderLineId);
            if (line is null)
                return Results.Problem(statusCode: 404, title: "Not Found",
                    type: "https://mersal.foundation/problems/not-found");

            // ONE session per call. The quantity is not a caller-supplied number: a centre recording "6" in a
            // single tap would collapse six separate deliveries — each with its own date, practitioner and
            // attendance — into one undifferentiated row, and design 45 §2b is explicit that they are consumed
            // "one by one as they are delivered".
            var result = await executor.ConsumeAsync(
                orderId, idem, gate.CallerProviderId!.Value,
                Guid.TryParse(me.Principal?.Subject, out var actorId) ? actorId : Guid.Empty,
                [new ConsumeLineRequest(req.OrderLineId, 1m)], clock.GetUtcNow(),
                // The house pattern for the service-owned transaction (pharmacy's DispenseExecutor set it):
                // ConsumeExecutor invokes this AFTER its write and BEFORE its commit, so the event joins the
                // same transaction while the payload is still built here, where the vocabulary belongs.
                // Wrapping the handler instead would nest a second transaction inside the executor's and throw.
                //
                // BRACED body, not an expression body — OutboxAtomicityTests recognises the exemption by the
                // callback's block, so `=> await outbox...` reads to it as a bare enqueue outside a
                // transaction. The braces are load-bearing for the check, not style.
                insideTransaction: async (o, fulfillments, innerCt) =>
                {
                    await outbox.EnqueueAsync("ProcedureSessionDelivered", "orders.events", new
                    {
                        tenantId = o.TenantId, orderId = o.OrderId, orderNo = o.OrderNo,
                        orderLineId = req.OrderLineId, providerId = gate.CallerProviderId,
                        deliveredAt = clock.GetUtcNow(), practitioner = req.DeliveringPractitioner,
                        attended = req.Attended, benefitCategory = BenefitCategoryMap.ForOrderType(o.OrderType),
                    }, innerCt);
                },
                ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "order_line", EntityId = req.OrderLineId.ToString(),
                Action = AuditAction.Consume, ActorUserId = me.Principal?.Subject,
                DecisionOutcome = result.Outcome.ToString(),
                DecisionReasonCode = $"procedure-session:{result.Outcome}",
                FieldClasses = ["phi"],
            }, ct);

            return result.Outcome switch
            {
                // A REPLAY returns 200 with the original progress — the same answer as the first call, which
                // is what makes a double-tap safe. Not 201: nothing new happened.
                ConsumeOutcome.Applied => Results.Ok(Progress(result.Order!, req.OrderLineId)),
                ConsumeOutcome.Replayed => Results.Ok(Progress(result.Order!, req.OrderLineId)),
                // THREE outcomes all mean "there is nothing left to deliver", and the centre needs the same
                // sentence for each. They differ only in HOW the line ran out: OverConsume is asking for more
                // than remains, AlreadyUsed is a line the last session completed, OrderNotConsumable is the
                // order closing behind it. Leaving the last two on the generic 409 told a receptionist with the
                // beneficiary in front of them "Conflict", which is not something anyone can act on.
                ConsumeOutcome.OverConsume or ConsumeOutcome.AlreadyUsed or ConsumeOutcome.OrderNotConsumable =>
                    Results.Problem(statusCode: 422, title: "no-sessions-remaining",
                        type: "urn:hbmp:no-sessions-remaining",
                        detail: "Every authorised session for this order has already been delivered. "
                              + "A further course needs a new order from the referring doctor."),
                ConsumeOutcome.OrderExpired => Results.Problem(statusCode: 422, title: "order-expired",
                    type: "urn:hbmp:order-expired",
                    detail: "This order's validity has passed; undelivered sessions are forfeited. "
                          + "The ordering doctor can request a revalidation."),
                ConsumeOutcome.IdempotencyKeyReuse => Results.Problem(statusCode: 409, title: "idempotency-key-reuse",
                    type: "urn:hbmp:idempotency-key-reuse",
                    detail: "That key was already used for a different session."),
                ConsumeOutcome.NotFound or ConsumeOutcome.LineNotFound => Results.Problem(
                    statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found"),
                _ => Results.Problem(statusCode: 409, title: result.Outcome.ToString(),
                    type: "urn:hbmp:session-not-recorded"),
            };
        }).RequireAuthorization(HbmpPolicies.Scope("procedure:consume"));

        // ---- Close the loop --------------------------------------------------------------------------------
        v1.MapPost("/{orderId:guid}/report", async (
            Guid orderId, CompletionReportRequest req, OrdersDbContext db, ProcedureProviderGate gate,
            IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock,
            CancellationToken ct) =>
        {
            var order = await Owned(db, gate.CallerProviderId).FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
            if (gate.AuthorizeOrder(order) is { } denied) return denied;

            // A REFERRAL cannot close without a report. An open referral loop is the classic outpatient
            // patient-safety failure — the beneficiary was sent somewhere and nobody ever learned what
            // happened — and an empty report body is an open loop wearing a closed one's clothes.
            if (string.IsNullOrWhiteSpace(req.Findings))
                return Results.Problem(statusCode: 422, title: "report-required",
                    type: "urn:hbmp:report-required",
                    detail: "A completion report must say what was found or done. The ordering doctor cannot "
                          + "close the loop on an empty report.");

            order!.CompletionReport = req.Findings.Trim();
            order.CompletionReportedBy = me.Principal?.Subject;
            order.CompletionReportedAt = clock.GetUtcNow();

            // ONE transaction around the state change AND its event. Enqueuing then saving without this leaves
            // a window in which a crash either loses the loop-closure event — so the ordering doctor's worklist
            // shows the referral open for ever, with a report sitting in the database nobody is told about — or
            // publishes closure for a report that was never committed. Caught by
            // Mersal.Architecture.Tests.OutboxAtomicityTests, which is the check that exists because this is
            // easy to write the other way and impossible to notice afterwards.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await outbox.EnqueueAsync("ProcedureLoopClosed", "orders.events", new
            {
                tenantId = order.TenantId, orderId = order.OrderId, orderNo = order.OrderNo,
                orderingProviderId = order.OrderingProviderId, reportedAt = order.CompletionReportedAt,
            }, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "investigation_order", EntityId = orderId.ToString(),
                Action = AuditAction.Update, ActorUserId = me.Principal?.Subject,
                DecisionOutcome = "loop-closed", FieldClasses = ["phi"],
            }, ct);

            return Results.Ok(new { orderId, closed = true, reportedAt = order.CompletionReportedAt });
        }).RequireAuthorization(HbmpPolicies.Scope("procedure:consume"));
    }

    /// <summary>
    /// THE ownership filter. Every read in this file starts here.
    ///
    /// <para>A single helper rather than a <c>.Where()</c> repeated per query, because the R3 defect was not
    /// that somebody wrote the ownership check wrongly — it was that the check was expressed somewhere it
    /// could be omitted without anything looking wrong. A null caller id yields an empty set, never the whole
    /// table.</para>
    /// </summary>
    private static IQueryable<InvestigationOrder> Owned(OrdersDbContext db, Guid? callerProviderId) =>
        callerProviderId is { } id
            ? db.Orders.Include(o => o.Lines).Where(o => o.AssignedProviderId == id)
            : db.Orders.Include(o => o.Lines).Where(_ => false);

    private static object Progress(InvestigationOrder order, Guid lineId)
    {
        var line = order.Lines.First(l => l.OrderLineId == lineId);
        var (delivered, authorised) = ProcedureSessions.Progress(line);
        return new
        {
            orderId = order.OrderId, orderLineId = lineId,
            sessionsDelivered = delivered, sessionsAuthorised = authorised,
            sessionsRemaining = Math.Max(0, authorised - delivered),
            progressLabel = $"{delivered} of {authorised} sessions delivered",
        };
    }

    private static async Task AuditRead(IAuditClient audit, IHbmpPrincipalAccessor me, string op, int count) =>
        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "investigation_order", EntityId = op, Action = AuditAction.Read,
            ActorUserId = me.Principal?.Subject, DecisionOutcome = "Allow",
            DecisionReasonCode = $"{op}:{count}", FieldClasses = ["phi"],
        });
}

/// <summary>One delivered session. The quantity is NOT a field — a session is one, by construction.</summary>
public sealed record RecordSessionRequest(
    Guid OrderLineId, string? DeliveringPractitioner, bool Attended = true, string? Note = null);

/// <summary>The report that closes a referral loop.</summary>
public sealed record CompletionReportRequest(string Findings);
