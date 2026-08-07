using Mersal.Amendment;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Api;

public sealed record CancelRxLineRequest(string ReasonCode, string? ReasonText);
public sealed record AmendRxLineRequest(decimal QuantityPrescribed, string ReasonCode, string? ReasonText);
public sealed record CancelRxLinesRequest(string ReasonCode, string? ReasonText);
public sealed record RxLineCancelReport(Guid PrescriptionLineId, string? DrugName, bool Cancelled, string? Refusal);

/// <summary>
/// 30.2 — amend and cancel SIGNED prescriptions (design 46 §1–§3). The medication twin of orders'
/// <c>AmendmentEndpoints</c>.
///
/// <para>The existing <c>POST /prescriptions/{id}/cancel</c> in <c>Prescriptions.cs</c> is rewritten onto
/// this path: it read-then-wrote, worked at prescription level, and took a free-text reason with no
/// idempotency key. Route, scope and gate are unchanged.</para>
/// </summary>
public static class RxAmendmentEndpoints
{
    public static void MapRxAmendment(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1/prescriptions").RequireAuthorization();

        v1.MapGet("/amendment-reasons", () =>
                Results.Ok(AmendmentReasons.For(ReasonScope.Prescription)
                    .Select(r => new { code = r.Code, nameEn = r.NameEn, nameAr = r.NameAr })))
            .RequireAuthorization(HbmpPolicies.Scope("rx:read"));

        // ---- Cancel ONE line ---------------------------------------------------------------------------
        v1.MapPost("/{rxId:guid}/lines/{lineId:guid}/cancel", async Task<IResult> (
            Guid rxId, Guid lineId, CancelRxLineRequest req, HttpRequest http, PharmacyDbContext db,
            PharmacyGate gate, AmendExecutor executor, IAuditClient audit, IOutbox outbox,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required",
                    type: "urn:hbmp:idempotency-required",
                    detail: "The key must be stable per INTENT, not per attempt: a double-tapped cancel must "
                          + "not write two amendment records.");

            var rx = await db.Prescriptions.AsNoTracking().FirstOrDefaultAsync(p => p.PrescriptionId == rxId, ct);
            if (rx is null) return NotFound();
            if (await gate.CheckAsync(PharmacyPolicies.RxCreate, "prescription", rxId.ToString(),
                    rx.BeneficiaryId, http.Headers.Authorization.ToString(), ct) is { } denied)
                return denied!;

            var actor = Guid.TryParse(me.Principal?.Subject, out var a) ? a : Guid.Empty;
            var result = await executor.CancelLineAsync(
                rxId, lineId, idem, new AmendReason(req.ReasonCode, req.ReasonText), actor,
                me.Principal?.DisplayName, clock.GetUtcNow(),
                // BRACED body — the enqueue joins the executor's transaction, and OutboxAtomicityTests
                // recognises the exemption by the block.
                insideTransaction: async (p, line, record, innerCt) =>
                {
                    await outbox.EnqueueAsync(RxAmendmentEvents.LineCancelled, RxAmendmentEvents.DomainStream,
                        RxAmendmentEvents.Domain(p, line, record, null), innerCt);
                    await outbox.EnqueueAsync(RxAmendmentEvents.LineCancelled, RxAmendmentEvents.NotificationQueue,
                        RxAmendmentEvents.Notification(p, line, record), innerCt);
                }, ct);

            await AuditAsync(audit, me, lineId, "Cancelled", req.ReasonCode, result.Outcome, ct);
            return Respond(result, rxId, lineId);
        }).RequireAuthorization(HbmpPolicies.Scope("rx:write"));

        // ---- Amend ONE line's quantity -----------------------------------------------------------------
        v1.MapPost("/{rxId:guid}/lines/{lineId:guid}/amend", async Task<IResult> (
            Guid rxId, Guid lineId, AmendRxLineRequest req, HttpRequest http, PharmacyDbContext db,
            PharmacyGate gate, AmendExecutor executor, IAuditClient audit, IOutbox outbox,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required",
                    type: "urn:hbmp:idempotency-required");

            var rx = await db.Prescriptions.AsNoTracking().FirstOrDefaultAsync(p => p.PrescriptionId == rxId, ct);
            if (rx is null) return NotFound();
            if (await gate.CheckAsync(PharmacyPolicies.RxCreate, "prescription", rxId.ToString(),
                    rx.BeneficiaryId, http.Headers.Authorization.ToString(), ct) is { } denied)
                return denied!;

            var actor = Guid.TryParse(me.Principal?.Subject, out var a) ? a : Guid.Empty;
            var result = await executor.AmendLineQuantityAsync(
                rxId, lineId, idem, req.QuantityPrescribed, new AmendReason(req.ReasonCode, req.ReasonText),
                actor, me.Principal?.DisplayName, clock.GetUtcNow(),
                insideTransaction: async (p, line, record, innerCt) =>
                {
                    await outbox.EnqueueAsync(RxAmendmentEvents.LineAmended, RxAmendmentEvents.DomainStream,
                        RxAmendmentEvents.Domain(p, line, record, record.NewLineId), innerCt);
                    await outbox.EnqueueAsync(RxAmendmentEvents.LineAmended, RxAmendmentEvents.NotificationQueue,
                        RxAmendmentEvents.Notification(p, line, record), innerCt);
                }, ct);

            await AuditAsync(audit, me, lineId, "Superseded", req.ReasonCode, result.Outcome, ct);
            return Respond(result, rxId, lineId);
        }).RequireAuthorization(HbmpPolicies.Scope("rx:write"));

        // ---- Cancel every still-cancellable line, reporting partial success plainly --------------------
        v1.MapPost("/{rxId:guid}/cancel-lines", async Task<IResult> (
            Guid rxId, CancelRxLinesRequest req, HttpRequest http, PharmacyDbContext db, PharmacyGate gate,
            AmendExecutor executor, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me,
            TimeProvider clock, CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required",
                    type: "urn:hbmp:idempotency-required");

            var rx = await db.Prescriptions.AsNoTracking().Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.PrescriptionId == rxId, ct);
            if (rx is null) return NotFound();
            if (await gate.CheckAsync(PharmacyPolicies.RxCreate, "prescription", rxId.ToString(),
                    rx.BeneficiaryId, http.Headers.Authorization.ToString(), ct) is { } denied)
                return denied!;

            var now = clock.GetUtcNow();
            var ctx = new AmendContext(
                HeadAmendable: PrescriptionWorkflow.CanAmendLines(rx.Status),
                Expired: rx.Status == RxStatus.Expired || (rx.ExpiresAt is { } e && e <= now));
            var plan = BulkCancel.Plan(
                [.. rx.Lines.Select(l => new AmendableLine(
                    l.PrescriptionLineId, l.IsTerminal, l.QuantityPrescribed, l.QuantityDispensed))],
                ctx);

            var actor = Guid.TryParse(me.Principal?.Subject, out var a) ? a : Guid.Empty;
            var reports = new List<RxLineCancelReport>();
            foreach (var outcome in plan.Outcomes)
            {
                var line = rx.Lines.First(l => l.PrescriptionLineId == outcome.LineId);
                if (!outcome.Cancellable)
                {
                    reports.Add(new RxLineCancelReport(
                        line.PrescriptionLineId, line.DrugName, false, Explain(outcome.Error)));
                    continue;
                }
                var result = await executor.CancelLineAsync(
                    rxId, line.PrescriptionLineId,
                    $"{idem}{IdempotencyKeyRules.Separator}{line.PrescriptionLineId}",
                    new AmendReason(req.ReasonCode, req.ReasonText), actor, me.Principal?.DisplayName, now,
                    insideTransaction: async (p, l, record, innerCt) =>
                    {
                        await outbox.EnqueueAsync(RxAmendmentEvents.LineCancelled, RxAmendmentEvents.DomainStream,
                            RxAmendmentEvents.Domain(p, l, record, null), innerCt);
                        await outbox.EnqueueAsync(RxAmendmentEvents.LineCancelled, RxAmendmentEvents.NotificationQueue,
                            RxAmendmentEvents.Notification(p, l, record), innerCt);
                    }, ct);

                var ok = result.Outcome is AmendOutcome.Applied or AmendOutcome.Replayed;
                reports.Add(new RxLineCancelReport(
                    line.PrescriptionLineId, line.DrugName, ok, ok ? null : Describe(result.Conflict)));
            }

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "prescription", EntityId = rxId.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "CancelLines",
                DecisionReasonCode = req.ReasonCode,
            }, ct);

            var cancelled = reports.Count(r => r.Cancelled);
            return cancelled == 0
                ? Results.Json(new { rxId, cancelled, lines = reports }, statusCode: 409)
                : cancelled < reports.Count
                    ? Results.Json(new { rxId, cancelled, lines = reports }, statusCode: 207)
                    : Results.Ok(new { rxId, cancelled, lines = reports });
        }).RequireAuthorization(HbmpPolicies.Scope("rx:write"));
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

    private static IResult Respond(AmendResult result, Guid rxId, Guid lineId) => result.Outcome switch
    {
        AmendOutcome.Applied => Results.Ok(new
        {
            rxId, prescriptionLineId = lineId, amendmentId = result.AmendmentId,
            newLineId = result.NewLineId, replayed = false,
        }),
        AmendOutcome.Replayed => Results.Ok(new
        {
            rxId, prescriptionLineId = lineId, amendmentId = result.AmendmentId,
            newLineId = result.NewLineId, replayed = true,
        }),
        AmendOutcome.NotFound or AmendOutcome.LineNotFound => NotFound(),

        AmendOutcome.AlreadyTerminal or AmendOutcome.Conflict => Results.Problem(
            statusCode: 409, title: "line-not-amendable", type: "urn:hbmp:line-not-amendable",
            detail: Describe(result.Conflict),
            extensions: new Dictionary<string, object?>
            {
                ["what"] = result.Conflict?.What,
                ["when"] = result.Conflict?.When,
                ["dispensingPharmacyId"] = result.Conflict?.DispensingPharmacyId,
                ["reasonCode"] = result.Conflict?.ReasonCode,
            }),

        AmendOutcome.RxNotAmendable => Results.Problem(
            statusCode: 409, title: "prescription-not-amendable", type: "urn:hbmp:rx-not-amendable",
            detail: "This prescription is in a status whose lines can no longer be changed."),
        AmendOutcome.Expired => Results.Problem(
            statusCode: 409, title: "prescription-expired", type: "urn:hbmp:rx-expired",
            detail: "This prescription is past its validity window, so it is expired rather than amendable."),
        AmendOutcome.BelowDispensed => Results.Problem(
            statusCode: 422, title: "below-dispensed", type: "urn:hbmp:amend-below-dispensed",
            detail: "The new quantity is less than what has already been handed over, which would imply "
                  + "un-dispensing it. Cancel the line instead to forfeit the remainder."),
        AmendOutcome.InvalidQuantity => Results.Problem(
            statusCode: 422, title: "invalid-quantity", type: "urn:hbmp:invalid-quantity",
            detail: "A quantity of zero is a cancellation, not an amendment — use the cancel endpoint."),
        AmendOutcome.NoChange => Results.Problem(
            statusCode: 422, title: "no-change", type: "urn:hbmp:amend-no-change",
            detail: "The amendment leaves the line exactly as it was."),
        AmendOutcome.InvalidReason => Results.Problem(
            statusCode: 422, title: "invalid-reason-code", type: "urn:hbmp:invalid-reason-code",
            detail: "The reason must be one of the coded values from GET /amendment-reasons."),
        AmendOutcome.InvalidIdempotencyKey => Results.Problem(
            statusCode: 400, title: "invalid-idempotency-key", type: "urn:hbmp:invalid-idempotency-key"),
        AmendOutcome.IdempotencyKeyReuse => Results.Problem(
            statusCode: 422, title: "idempotency-key-reuse", type: "urn:hbmp:idempotency-key-reuse",
            detail: "That key was already used for a different request."),
        _ => Results.Problem(statusCode: 409, title: "amendment-failed"),
    };

    private static string Describe(AmendConflict? c) => c switch
    {
        null => "This line can no longer be changed.",
        { What: "Dispensed", When: { } at } =>
            $"This line was dispensed at {at:yyyy-MM-dd HH:mm} UTC and can no longer be changed.",
        { What: var what, When: { } at, ReasonCode: { } code } =>
            $"This line was already {what.ToLowerInvariant()} at {at:yyyy-MM-dd HH:mm} UTC ({code}).",
        { What: var what } => $"This line is already {what.ToLowerInvariant()}.",
    };

    private static string Explain(AmendabilityError error) => error switch
    {
        AmendabilityError.AlreadyTerminal =>
            "Already dispensed, cancelled or amended — that part is a fact and cannot be withdrawn.",
        AmendabilityError.Expired => "The prescription is past its validity window.",
        AmendabilityError.OrderNotAmendable =>
            "The prescription is in a status whose lines can no longer change.",
        _ => "Not cancellable.",
    };

    private static async Task AuditAsync(
        IAuditClient audit, IHbmpPrincipalAccessor me, Guid lineId, string to, string reasonCode,
        AmendOutcome outcome, CancellationToken ct) =>
        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "prescription_line", EntityId = lineId.ToString(), Action = AuditAction.StateChange,
            ActorUserId = me.Principal?.Subject,
            DecisionOutcome = outcome == AmendOutcome.Applied ? to : outcome.ToString(),
            DecisionReasonCode = reasonCode, FieldClasses = ["phi"],
        }, ct);
}
