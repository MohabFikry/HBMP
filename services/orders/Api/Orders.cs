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
            IExaminationTypeResolver examTypes, IProcedureTypeResolver procedureTypes, OrderRoutingOptions routing,
            OrderNoIssuer orderNos, IAuditClient audit,
            IOutbox outbox, IHbmpPrincipalAccessor me, BranchScopeState branch, IValidityPolicySource validity,
            TimeProvider clock, CancellationToken ct) =>
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
                // 31.1 — whichever field the caller used to state the amount. A pre-31.1 caller sends
                // `quantityOrdered`; a 31.1 one sends `quantityPerSession` and leaves the other at zero,
                // and reading only the first would refuse every course composed by the new client.
                if ((line.QuantityPerSession ?? line.QuantityOrdered) <= 0)
                    return Results.Problem(statusCode: 422, title: "invalid-quantity", type: "urn:hbmp:invalid-quantity",
                        detail: $"Line '{line.Code}' must have a quantity greater than zero.");
                if (!await codes.IsValidAsync(line.CodeSystem, line.Code, bearer, ct))
                    return Results.Problem(statusCode: 422, title: "unknown-code", type: "urn:hbmp:unknown-code",
                        detail: $"{line.CodeSystem} code '{line.Code}' is not present in master data.");

                // The SECTION rule, re-derived here rather than trusted from step 1. The composing screen
                // shows it too, but that verdict is display state: a chest x-ray submitted on a lab order
                // would land in a haematology worklist where nobody can perform it, and the only place that
                // can actually be prevented is the write path.
                var mismatched = req.OrderType switch
                {
                    OrderType.Lab when InvestigationChecks.IsRadiology(line.Code) => "a radiology procedure on a laboratory order",
                    // 29.1 — both spellings, until the legacy value is dropped (design 45 §1).
                    OrderType.Imaging or OrderType.Radiology when InvestigationChecks.IsLaboratory(line.Code)
                        => "a laboratory procedure on a radiology order",
                    _ => null,
                };
                if (mismatched is not null)
                    return Results.Problem(statusCode: 422, title: "wrong-section", type: "urn:hbmp:wrong-section",
                        detail: $"'{line.Code}' is {mismatched}. It would reach a queue that cannot perform it.");
            }

            // 29.2 — PROCEDURE TYPE against the CODE, on the write path (design 45 §2).
            //
            // The composer checks this too, and that verdict is display state — the same reasoning the section
            // check above is written under. Design 45 §2 is explicit about the cost of skipping it: "left
            // unvalidated the field becomes decorative, and any reporting built on it is quietly wrong", which
            // is worse than having no field at all, because the reports still render.
            //
            // 31.1 — the KIND and the SESSION COUNT are the ORDER's, so they are read from the order and
            // checked against EVERY line's section. A per-line kind let a two-item course carry two kinds
            // and two session counts, which is not a course any centre can deliver. A line-level code is
            // still accepted from a pre-31.1 caller and still validated — an accepted-but-ignored type field
            // is decorative, and every report built on it would be quietly wrong.
            foreach (var line in req.Lines)
            {
                var typeCode = req.ProcedureTypeCode ?? line.ProcedureTypeCode;

                // Skip the round-trip entirely when there is nothing to check: a Lab/Radiology line with no
                // type is the overwhelmingly common case, and it is already correct by construction.
                if (OrderTypes.Canonical(req.OrderType) != OrderType.Procedure
                    && string.IsNullOrWhiteSpace(typeCode)) continue;

                var lookup = await procedureTypes.ResolveAsync(
                    typeCode, line.CodeSystem == CodeSystem.CPT ? line.Code : null, bearer, ct);

                // The count the session rules are checked against is the COURSE LENGTH, not the metered
                // total: "at most 12 sessions" is a statement about attendances, and comparing it to
                // sessions x per-session would refuse a perfectly ordinary 6-session course of a 3-per-visit
                // item as though 18 sessions had been asked for.
                var sessionCount = req.Sessions ?? line.QuantityOrdered;

                var procError = ProcedureLineChecks.Validate(
                    req.OrderType, typeCode, lookup.Section, sessionCount, lookup.Facts);
                if (procError != ProcedureLineError.None)
                {
                    var (en, ar) = ProcedureLineChecks.Explain(
                        procError, typeCode, line.Code, lookup.Section, lookup.Facts);
                    return Results.Problem(statusCode: 422, title: "procedure-type-invalid",
                        type: "urn:hbmp:procedure-type-invalid", detail: en,
                        extensions: new Dictionary<string, object?> { ["reason"] = procError.ToString(), ["detailAr"] = ar });
                }
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

            /*
             * WHEN THIS ORDER STOPS BEING ACTIONABLE.
             *
             * `expires_at` and the ix_order_expiry index have been in migration 0001 since the beginning and
             * nothing has ever written to them, so every investigation order this platform has issued is
             * valid for ever. A lab or imaging request is a clinical question asked on a particular day; a
             * technician acting on a six-month-old one is answering a question that may no longer be asked.
             *
             * Each order type carries its OWN configured period — a follow-up scan and a same-week blood
             * panel do not go stale at the same rate. A client-supplied `ExpiresAt` may only shorten it;
             * nobody grants themselves a longer window by putting a date in a request body.
             */
            var artefact = req.OrderType switch
            {
                OrderType.Lab => ValidityArtefact.LabOrder,
                // 29.1 — the new enum value MUST be named here. Left to the `_` arm a Radiology order would
                // silently take the PROCEDURE validity period, which is the exact class of defect an additive
                // enum value creates: the compiler stays green and the wrong number is used.
                OrderType.Imaging or OrderType.Radiology => ValidityArtefact.ImagingOrder,
                _ => ValidityArtefact.ProcedureOrder,
            };
            var policyExpiry = ValidityPolicy.ExpiryFor(now, await validity.DaysAsync(artefact, bearer, ct));
            var expiresAt = req.ExpiresAt is { } requested && requested < policyExpiry ? requested : policyExpiry;

            var order = new InvestigationOrder
            {
                OrderId = Guid.NewGuid(), OrderNo = await orderNos.NextAsync(now.Year, ct),
                BeneficiaryId = req.BeneficiaryId, EncounterId = req.EncounterId, OrderingProviderId = providerId,
                OrderingBranchId = branch.Context.ActiveBranchId,   // phase 14.4 — pin the raising branch
                OrderType = req.OrderType, Status = OrderStatus.Requested, RequestedAt = now, ExpiresAt = expiresAt,
                IdempotencyKey = idem, CreatedBy = actor,
                // 31.1 — the course: one kind and one session count for the whole order.
                ProcedureTypeCode = req.ProcedureTypeCode,
                Sessions = req.Sessions,
                Lines = req.Lines.Select(l => new OrderLine
                {
                    OrderLineId = Guid.NewGuid(), CodeSystem = l.CodeSystem, Code = l.Code,
                    Description = l.Description,
                    // 31.1 — the METERED TOTAL, derived: sessions x what is delivered at each attendance.
                    // `quantity_ordered` keeps its meaning exactly, which is what leaves the atomic consume
                    // path, the partial-approval arithmetic and the centre's queue untouched.
                    QuantityPerSession = l.QuantityPerSession ?? l.QuantityOrdered,
                    QuantityOrdered = ProcedureCourse.MeteredTotal(
                        req.Sessions, l.QuantityPerSession ?? l.QuantityOrdered),
                    Status = OrderLineStatus.Active,
                    // 29.2 — what was ASKED FOR, pinned at creation and never rewritten. On an auto-activated
                    // order the two are equal; when the order is routed to approval, QuantityOrdered is later
                    // narrowed to the APPROVED scope while this stays put (ProcedureSessions.ApplyApproval).
                    RequestedQuantity = ProcedureCourse.MeteredTotal(
                        req.Sessions, l.QuantityPerSession ?? l.QuantityOrdered),
                    // Still written, so a rollback to the previous build finds the data it expects. The
                    // ORDER's code is the one that is read.
                    ProcedureTypeCode = req.ProcedureTypeCode ?? l.ProcedureTypeCode,
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
            //
            // `encounterId` — ADR-0031. The order has carried the column since phase 4 and never put it on the
            // wire, so the visit that caused the order and the order itself were two facts with nothing
            // joining them: "what did this consultation order?" had no answer, and emr's episode timeline
            // could not record a step it had no way to attach. `orderedByUserId` is the same argument for
            // WHO — a step with no actor cannot answer the question a timeline is opened to answer.
            await outbox.EnqueueAsync("OrderCreated", "orders.events",
                new
                {
                    tenantId = order.TenantId, orderId = order.OrderId, order.OrderNo,
                    beneficiaryId = order.BeneficiaryId, encounterId = order.EncounterId,
                    orderType = order.OrderType.ToString(), orderedByUserId = order.CreatedBy,
                }, ct);
            if (route.RouteToApproval)
                // `orderedByUserId` carries the ordering clinician to whoever ingests this into approvals
                // (§11.3). The authorization's `CreatedBy` is what a decision notice is addressed to, and on
                // the ingest seam the caller is a machine principal — so without this the answer to "was my
                // order approved?" has no human to reach, and the clinician has to go and look.
                await outbox.EnqueueAsync("OrderPendingApproval", "orders.events",
                    new
                    {
                        tenantId = order.TenantId, orderId = order.OrderId, order.OrderNo, reason = route.Reason,
                        beneficiaryId = order.BeneficiaryId, encounterId = order.EncounterId,
                        orderedByUserId = order.CreatedBy,
                        // WHO IS ASKING and WHAT FOR — the two facts an authorization cannot be created
                        // without, added when this event became the routing feed's input
                        // (ApprovalRoutingFeed). `serviceCodes` is not decoration: a partial approval must be
                        // a strict subset of the requested codes, so an authorization ingested without them
                        // can be approved or rejected outright and never narrowed, which is the decision the
                        // approval team most often wants to make.
                        providerId = order.OrderingProviderId == Guid.Empty ? (Guid?)null : order.OrderingProviderId,
                        serviceCodes = order.Lines.Select(l => l.Code).Distinct(StringComparer.Ordinal).ToArray(),
                    }, ct);
            else
                await outbox.EnqueueAsync("OrderActivated", "orders.events",
                    new { tenantId = order.TenantId, orderId = order.OrderId, order.OrderNo }, ct);
            await tx.CommitAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "investigation_order", EntityId = order.OrderId.ToString(), Action = AuditAction.Create,
                ActorUserId = actor, DecisionOutcome = order.Status.ToString(), DecisionReasonCode = route.Reason,
                AfterState = $"{{\"orderNo\":\"{order.OrderNo}\",\"status\":\"{order.Status}\"}}",
            }, ct);

            return Results.Created($"/api/v1/investigation-orders/{order.OrderId}", OrderResponse.From(order));
        }).RequireAuthorization(HbmpPolicies.Scope("orders:write"))
        .Produces<OrderResponse>();

        // ---- Read (treating clinician) ----
        v1.MapGet("/{id:guid}", async (Guid id, HttpRequest http, OrdersDbContext db, OrdersGate gate, CancellationToken ct) =>
        {
            var order = await db.Orders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.OrderId == id, ct);
            if (order is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            var denied = await gate.CheckAsync(OrdersPolicies.Read, id.ToString(), order.BeneficiaryId, http.Headers.Authorization.ToString(), ct);
            if (denied is not null) return denied;
            return Results.Ok(OrderResponse.From(order));
        })
        .Produces<OrderResponse>();

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
            // 14.4 — a BranchScoped clinician sees only orders raised in the active branch; 25.1 — a
            // set-scoped caller sees every branch they hold a grant to, and never more.
            q = q.ApplyBranchScope(o => o.OrderingBranchId, (me.Principal is null ? ScopeMode.MemberScoped : BranchScopeModes.ModeFor(me.Principal)), branch.Context);
            var rows = await q.OrderByDescending(o => o.RequestedAt).Take(100).ToListAsync(ct);
            return Results.Ok(rows.Select(OrderResponse.From));
        }).RequireAuthorization(HbmpPolicies.Scope("orders:read"))
        .Produces<IEnumerable<OrderResponse>>();

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

            // 24.3 — the cancellation and the event announcing it commit together. Without the transaction
            // a crash between the two commits leaves an order cancelled that pharmacy, approvals and billing
            // still believe is live, and no retry produces the event because nothing records it was owed.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            order.Status = OrderStatus.Cancelled;
            foreach (var l in order.Lines.Where(l => l.Status == OrderLineStatus.Active)) l.Status = OrderLineStatus.Cancelled;
            await db.SaveChangesAsync(ct);
            // ADR-0031: a cancelled order ADDS a step beside its OrderPlaced — an episode records what
            // happened, so it never retracts one. `orderNo` rides along because the step's reference is a
            // business key: ORD-2026-000014 is a thing a desk can say out loud and look up, and an internal
            // uuid is neither.
            await outbox.EnqueueAsync("OrderCancelled", "orders.events", new
            {
                tenantId = order.TenantId, orderId = order.OrderId, order.OrderNo,
                beneficiaryId = order.BeneficiaryId, encounterId = order.EncounterId,
                cancelledByUserId = me.Principal?.Subject, reason = req.Reason,
            }, ct);
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "investigation_order", EntityId = order.OrderId.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "Cancelled", DecisionReasonCode = req.Reason,
            }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(OrderResponse.From(order));
        }).RequireAuthorization(HbmpPolicies.Scope("orders:write"))
        .Produces<OrderResponse>();
    }
}
