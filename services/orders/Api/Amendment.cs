using Mersal.Amendment;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Api;

/// <summary>Cancel one line. The reason code is mandatory; the free text is additional, never instead.</summary>
public sealed record CancelLineRequest(string ReasonCode, string? ReasonText);

/// <summary>Amend one line's quantity. Everything else about the line is copied to the new version — an
/// amendment changes ONE thing (design 46 §1).</summary>
public sealed record AmendLineRequest(decimal QuantityOrdered, string ReasonCode, string? ReasonText);

/// <summary>Cancel every still-cancellable line. Partial success is reported plainly (design 46 §3).</summary>
public sealed record CancelOrderLinesRequest(string ReasonCode, string? ReasonText);

/// <summary>One line's fate in a whole-order cancel — named, with a reason when it could not be cancelled.</summary>
public sealed record LineCancelReport(Guid OrderLineId, string Code, bool Cancelled, string? Refusal);

/// <summary>
/// 30.2 — amend and cancel SIGNED orders (design 46 §1–§3).
///
/// <para>The order-level <c>POST /investigation-orders/{id}/cancel</c> in <c>Orders.cs</c> is REWRITTEN onto
/// this path rather than left beside it. It had three defects and all three are the ones this phase exists
/// to fix: it read the status and then wrote (the lost update design 46 §2 is about); it worked at order
/// level, so a partly-fulfilled order was all-or-nothing; and it took a free-text reason with no
/// idempotency key. The route, the scope and the ABAC gate are unchanged — a caller sees a stricter,
/// better-explained endpoint at the same address.</para>
/// </summary>
public static class AmendmentEndpoints
{
    public static void MapAmendment(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1/investigation-orders").RequireAuthorization();

        // ---- The coded vocabulary the picker renders ---------------------------------------------------
        v1.MapGet("/amendment-reasons", () =>
                Results.Ok(AmendmentReasons.For(ReasonScope.Order)
                    .Select(r => new { code = r.Code, nameEn = r.NameEn, nameAr = r.NameAr })))
            .RequireAuthorization(HbmpPolicies.Scope("orders:read"));

        // ---- Cancel ONE line ---------------------------------------------------------------------------
        v1.MapPost("/{orderId:guid}/lines/{lineId:guid}/cancel", async Task<IResult> (
            Guid orderId, Guid lineId, CancelLineRequest req, HttpRequest http, OrdersDbContext db,
            OrdersGate gate, AmendExecutor executor, IAuditClient audit, IOutbox outbox,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required",
                    type: "urn:hbmp:idempotency-required",
                    detail: "The key must be stable per INTENT, not per attempt: a double-tapped cancel must "
                          + "not write two amendment records.");

            var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
            if (order is null) return NotFound();
            if (await gate.CheckAsync(OrdersPolicies.Create, orderId.ToString(), order.BeneficiaryId,
                    http.Headers.Authorization.ToString(), ct) is { } denied) return denied;

            var actor = Guid.TryParse(me.Principal?.Subject, out var a) ? a : Guid.Empty;
            var result = await executor.CancelLineAsync(
                orderId, lineId, idem, new AmendReason(req.ReasonCode, req.ReasonText), actor,
                me.Principal?.DisplayName, clock.GetUtcNow(),
                // BRACED body: the outbox enqueue joins the executor's transaction, so the state change and
                // the event announcing it are one fact or neither. OutboxAtomicityTests recognises the
                // exemption by the block — the braces are load-bearing for the check, not style.
                insideTransaction: async (o, line, record, innerCt) =>
                {
                    await outbox.EnqueueAsync(AmendmentEvents.LineCancelled, AmendmentEvents.DomainStream,
                        AmendmentEvents.Domain(o, line, record, null), innerCt);
                    await outbox.EnqueueAsync(AmendmentEvents.LineCancelled, AmendmentEvents.NotificationQueue,
                        AmendmentEvents.Notification(o, line, record), innerCt);
                }, ct);

            await AuditAsync(audit, me, lineId, "Cancelled", req.ReasonCode, result.Outcome, ct);
            return Respond(result, orderId, lineId);
        }).RequireAuthorization(HbmpPolicies.Scope("orders:write"));

        // ---- Amend ONE line's quantity -----------------------------------------------------------------
        v1.MapPost("/{orderId:guid}/lines/{lineId:guid}/amend", async Task<IResult> (
            Guid orderId, Guid lineId, AmendLineRequest req, HttpRequest http, OrdersDbContext db,
            OrdersGate gate, AmendExecutor executor, IAuditClient audit, IOutbox outbox,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required",
                    type: "urn:hbmp:idempotency-required");

            var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
            if (order is null) return NotFound();
            if (await gate.CheckAsync(OrdersPolicies.Create, orderId.ToString(), order.BeneficiaryId,
                    http.Headers.Authorization.ToString(), ct) is { } denied) return denied;

            var actor = Guid.TryParse(me.Principal?.Subject, out var a) ? a : Guid.Empty;
            var result = await executor.AmendLineQuantityAsync(
                orderId, lineId, idem, req.QuantityOrdered, new AmendReason(req.ReasonCode, req.ReasonText),
                actor, me.Principal?.DisplayName, clock.GetUtcNow(),
                insideTransaction: async (o, line, record, innerCt) =>
                {
                    await outbox.EnqueueAsync(AmendmentEvents.LineAmended, AmendmentEvents.DomainStream,
                        AmendmentEvents.Domain(o, line, record, record.NewLineId), innerCt);
                    await outbox.EnqueueAsync(AmendmentEvents.LineAmended, AmendmentEvents.NotificationQueue,
                        AmendmentEvents.Notification(o, line, record), innerCt);
                }, ct);

            await AuditAsync(audit, me, lineId, "Superseded", req.ReasonCode, result.Outcome, ct);
            return Respond(result, orderId, lineId);
        }).RequireAuthorization(HbmpPolicies.Scope("orders:write"));

        // ---- Cancel the WHOLE order = cancel every still-cancellable line ------------------------------
        //
        // Design 46 §3: "if some lines are already consumed it reports PARTIAL SUCCESS plainly rather than
        // failing the lot or silently doing half." Both failure modes are worse than the truth — failing the
        // lot leaves a doctor unable to withdraw anything; doing half leaves them believing they have.
        v1.MapPost("/{orderId:guid}/cancel-lines", async Task<IResult> (
            Guid orderId, CancelOrderLinesRequest req, HttpRequest http, OrdersDbContext db, OrdersGate gate,
            AmendExecutor executor, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me,
            TimeProvider clock, CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required",
                    type: "urn:hbmp:idempotency-required");

            var order = await db.Orders.AsNoTracking().Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
            if (order is null) return NotFound();
            if (await gate.CheckAsync(OrdersPolicies.Create, orderId.ToString(), order.BeneficiaryId,
                    http.Headers.Authorization.ToString(), ct) is { } denied) return denied;

            var now = clock.GetUtcNow();
            var ctx = new AmendContext(
                HeadAmendable: OrderWorkflow.CanAmendLines(order.Status),
                Expired: order.Status == OrderStatus.Expired || (order.ExpiresAt is { } e && e <= now));
            var plan = BulkCancel.Plan(
                [.. order.Lines.Select(l =>
                    new AmendableLine(l.OrderLineId, l.IsTerminal, l.QuantityOrdered, l.QuantityConsumed))],
                ctx);

            var actor = Guid.TryParse(me.Principal?.Subject, out var a) ? a : Guid.Empty;
            var reports = new List<LineCancelReport>();
            foreach (var outcome in plan.Outcomes)
            {
                var line = order.Lines.First(l => l.OrderLineId == outcome.LineId);
                if (!outcome.Cancellable)
                {
                    reports.Add(new LineCancelReport(line.OrderLineId, line.Code, false, Explain(outcome.Error)));
                    continue;
                }
                // Per-line key derived from the caller's, so the whole-order cancel is replayable as a unit
                // AND each line keeps its own duplicate-proof anchor. The same composition the consume path
                // uses for its multi-line key.
                var result = await executor.CancelLineAsync(
                    orderId, line.OrderLineId, $"{idem}{IdempotencyKeyRules.Separator}{line.OrderLineId}",
                    new AmendReason(req.ReasonCode, req.ReasonText), actor, me.Principal?.DisplayName, now,
                    insideTransaction: async (o, l, record, innerCt) =>
                    {
                        await outbox.EnqueueAsync(AmendmentEvents.LineCancelled, AmendmentEvents.DomainStream,
                            AmendmentEvents.Domain(o, l, record, null), innerCt);
                        await outbox.EnqueueAsync(AmendmentEvents.LineCancelled, AmendmentEvents.NotificationQueue,
                            AmendmentEvents.Notification(o, l, record), innerCt);
                    }, ct);

                reports.Add(new LineCancelReport(line.OrderLineId, line.Code,
                    result.Outcome is AmendOutcome.Applied or AmendOutcome.Replayed,
                    result.Outcome is AmendOutcome.Applied or AmendOutcome.Replayed
                        ? null : ExplainOutcome(result)));
            }

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "investigation_order", EntityId = orderId.ToString(),
                Action = AuditAction.StateChange, ActorUserId = me.Principal?.Subject,
                DecisionOutcome = "CancelLines", DecisionReasonCode = req.ReasonCode,
            }, ct);

            var cancelled = reports.Count(r => r.Cancelled);
            // 207 for a genuinely mixed result. A 200 would report a partial withdrawal as a complete one,
            // which is the failure design 46 §3 names; a 409 would refuse work that succeeded.
            return cancelled == 0
                ? Results.Json(new { orderId, cancelled, lines = reports }, statusCode: 409)
                : cancelled < reports.Count
                    ? Results.Json(new { orderId, cancelled, lines = reports }, statusCode: 207)
                    : Results.Ok(new { orderId, cancelled, lines = reports });
        }).RequireAuthorization(HbmpPolicies.Scope("orders:write"));
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

    /// <summary>
    /// The refusal, in the words the doctor needs. Design 46 §2: a bare "someone else changed this" gets
    /// retried, and a retry after a dispense is how a cancelled-then-dispensed drug happens.
    /// </summary>
    private static IResult Respond(AmendResult result, Guid orderId, Guid lineId) => result.Outcome switch
    {
        AmendOutcome.Applied => Results.Ok(new
        {
            orderId, orderLineId = lineId, amendmentId = result.AmendmentId, newLineId = result.NewLineId,
            replayed = false,
        }),
        AmendOutcome.Replayed => Results.Ok(new
        {
            orderId, orderLineId = lineId, amendmentId = result.AmendmentId, newLineId = result.NewLineId,
            replayed = true,
        }),
        AmendOutcome.NotFound or AmendOutcome.LineNotFound => NotFound(),

        AmendOutcome.AlreadyTerminal or AmendOutcome.Conflict => Results.Problem(
            statusCode: 409, title: "line-not-amendable", type: "urn:hbmp:line-not-amendable",
            detail: Describe(result.Conflict),
            extensions: new Dictionary<string, object?>
            {
                ["what"] = result.Conflict?.What,
                ["when"] = result.Conflict?.When,
                ["performedByProviderId"] = result.Conflict?.PerformedByProviderId,
                ["reasonCode"] = result.Conflict?.ReasonCode,
            }),

        AmendOutcome.OrderNotAmendable => Results.Problem(
            statusCode: 409, title: "order-not-amendable", type: "urn:hbmp:order-not-amendable",
            detail: "This order is in a status whose lines can no longer be changed."),
        AmendOutcome.Expired => Results.Problem(
            statusCode: 409, title: "order-expired", type: "urn:hbmp:order-expired",
            detail: "This order is past its validity window, so it is expired rather than amendable. The "
                  + "approval team can revalidate it."),
        AmendOutcome.BelowConsumed => Results.Problem(
            statusCode: 422, title: "below-consumed", type: "urn:hbmp:amend-below-consumed",
            detail: "The new quantity is less than what has already been delivered, which would imply "
                  + "un-delivering it. Cancel the line instead to forfeit the remainder."),
        AmendOutcome.InvalidQuantity => Results.Problem(
            statusCode: 422, title: "invalid-quantity", type: "urn:hbmp:invalid-quantity",
            detail: "A quantity of zero is a cancellation, not an amendment — use the cancel endpoint."),
        AmendOutcome.NoChange => Results.Problem(
            statusCode: 422, title: "no-change", type: "urn:hbmp:amend-no-change",
            detail: "The amendment leaves the line exactly as it was."),
        AmendOutcome.InvalidReason => Results.Problem(
            statusCode: 422, title: "invalid-reason-code", type: "urn:hbmp:invalid-reason-code",
            detail: "The reason must be one of the coded values from GET /amendment-reasons. Free text is "
                  + "additional, never instead."),
        AmendOutcome.InvalidIdempotencyKey => Results.Problem(
            statusCode: 400, title: "invalid-idempotency-key", type: "urn:hbmp:invalid-idempotency-key"),
        AmendOutcome.IdempotencyKeyReuse => Results.Problem(
            statusCode: 422, title: "idempotency-key-reuse", type: "urn:hbmp:idempotency-key-reuse",
            detail: "That key was already used for a different request. Answering it with the first "
                  + "amendment would report a change to a line you did not ask about."),
        _ => Results.Problem(statusCode: 409, title: "amendment-failed"),
    };

    private static string Describe(AmendConflict? c) => c switch
    {
        null => "This line can no longer be changed.",
        { What: "Consumed", When: { } at } =>
            $"This line was fulfilled at {at:yyyy-MM-dd HH:mm} UTC and can no longer be changed.",
        { What: var what, When: { } at, ReasonCode: { } code } =>
            $"This line was already {what.ToLowerInvariant()} at {at:yyyy-MM-dd HH:mm} UTC ({code}).",
        { What: var what } => $"This line is already {what.ToLowerInvariant()}.",
    };

    private static string Explain(AmendabilityError error) => error switch
    {
        AmendabilityError.AlreadyTerminal =>
            "Already delivered, cancelled or amended — that part is a fact and cannot be withdrawn.",
        AmendabilityError.Expired => "The order is past its validity window.",
        AmendabilityError.OrderNotAmendable => "The order is in a status whose lines can no longer change.",
        _ => "Not cancellable.",
    };

    private static string ExplainOutcome(AmendResult result) =>
        result.Outcome == AmendOutcome.Conflict
            ? "Someone changed this line while the request was in flight."
            : Describe(result.Conflict);

    private static async Task AuditAsync(
        IAuditClient audit, IHbmpPrincipalAccessor me, Guid lineId, string to, string reasonCode,
        AmendOutcome outcome, CancellationToken ct) =>
        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "order_line", EntityId = lineId.ToString(), Action = AuditAction.StateChange,
            ActorUserId = me.Principal?.Subject,
            DecisionOutcome = outcome == AmendOutcome.Applied ? to : outcome.ToString(),
            DecisionReasonCode = reasonCode, FieldClasses = ["phi"],
        }, ct);
}
