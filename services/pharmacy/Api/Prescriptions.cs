using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Api;

/// <summary>Phase 4.3 e-prescription endpoints: create+submit (treating-gated, drug-validated, advisory
/// interaction/allergy alerts, config-driven approval routing) and read/cancel. Order + lines + state change are
/// written in one transaction; RxCreated then RxSubmitted (and RxApproved when auto-approved) are enqueued to the
/// outbox. Alerts are advisory (recorded, never blocking). Every mutation is audited.</summary>
public static class PrescriptionEndpoints
{
    public static void MapPrescriptions(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1/prescriptions").RequireAuthorization();

        v1.MapPost("", async (
            CreatePrescriptionRequest req, HttpRequest http, PharmacyDbContext db, PharmacyGate gate,
            IDrugValidator drugs, IPrescribingScreener screener, RxRoutingOptions routing, SequenceIssuer seq,
            IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required", type: "urn:hbmp:idempotency-required");

            var existing = await db.Prescriptions.AsNoTracking().Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.IdempotencyKey == idem, ct);
            if (existing is not null) return Results.Ok(PrescriptionResponse.From(existing));

            if (req.Lines is null || req.Lines.Count == 0)
                return Results.Problem(statusCode: 400, title: "a prescription must have at least one line", type: "urn:hbmp:empty-rx");

            var bearer = http.Headers.Authorization.ToString();

            var denied = await gate.CheckAsync(PharmacyPolicies.RxCreate, "prescription", null, req.BeneficiaryId, bearer, ct);
            if (denied is not null) return denied;

            foreach (var l in req.Lines)
            {
                if (l.QuantityPrescribed <= 0 || l.RefillsAllowed < 0)
                    return Results.Problem(statusCode: 422, title: "invalid-line", type: "urn:hbmp:invalid-line",
                        detail: $"Drug '{l.DrugId}' needs quantityPrescribed > 0 and refillsAllowed ≥ 0.");
                if (!await drugs.DrugExistsAsync(l.DrugId, bearer, ct))
                    return Results.Problem(statusCode: 422, title: "unknown-drug", type: "urn:hbmp:unknown-drug",
                        detail: $"Drug '{l.DrugId}' is not present in master data.");
            }

            var now = clock.GetUtcNow();
            var actor = me.Principal?.Subject;
            var prescriberId = Guid.TryParse(me.Principal?.ProviderId, out var pg) ? pg : Guid.Empty;

            var rx = new Prescription
            {
                PrescriptionId = Guid.NewGuid(), RxNo = RxNo.Format(now.Year, await seq.NextAsync("rx_seq", now.Year, ct)),
                BeneficiaryId = req.BeneficiaryId, EncounterId = req.EncounterId, PrescriberId = prescriberId,
                Status = RxStatus.Draft, ExpiresAt = req.ExpiresAt, IdempotencyKey = idem, CreatedBy = actor,
                Lines = req.Lines.Select(l => new PrescriptionLine
                {
                    PrescriptionLineId = Guid.NewGuid(), DrugId = l.DrugId, Dose = l.Dose, Route = l.Route,
                    Frequency = l.Frequency, QuantityPrescribed = l.QuantityPrescribed, RefillsAllowed = l.RefillsAllowed,
                    Status = RxLineStatus.Active,
                }).ToList(),
            };

            // Advisory alerts (non-blocking): screen interactions + allergies, record with acknowledgement.
            var screening = await screener.ScreenAsync(req.BeneficiaryId, rx.Lines.Select(l => l.DrugId).ToList(), bearer, ct);

            // Draft → Submitted; then route: gated → stays Submitted (awaiting approval); else auto-approve.
            rx.Status = RxStatus.Submitted; rx.SubmittedAt = now;
            var route = RxRoutingPolicy.Evaluate(rx, routing);
            if (!route.RequiresApproval) rx.Status = RxStatus.Approved;

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Prescriptions.Add(rx);
            foreach (var a in screening.Alerts)
                db.PrescriptionAlerts.Add(new PrescriptionAlert
                {
                    AlertId = Guid.NewGuid(), PrescriptionId = rx.PrescriptionId, Kind = a.Kind.ToString(),
                    Severity = a.Severity, Detail = a.Detail, Acknowledged = req.AcknowledgeAlerts, RaisedAt = now,
                });
            await db.SaveChangesAsync(ct);

            await outbox.EnqueueAsync("RxCreated", "pharmacy.events",
                new { tenantId = rx.TenantId, prescriptionId = rx.PrescriptionId, rx.RxNo, beneficiaryId = rx.BeneficiaryId }, ct);
            await outbox.EnqueueAsync("RxSubmitted", "pharmacy.events",
                new { tenantId = rx.TenantId, prescriptionId = rx.PrescriptionId, rx.RxNo, requiresApproval = route.RequiresApproval }, ct);
            if (rx.Status == RxStatus.Approved)
                await outbox.EnqueueAsync("RxApproved", "pharmacy.events", new { tenantId = rx.TenantId, prescriptionId = rx.PrescriptionId, rx.RxNo, auto = true }, ct);
            await tx.CommitAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "prescription", EntityId = rx.PrescriptionId.ToString(), Action = AuditAction.Create,
                ActorUserId = actor, DecisionOutcome = rx.Status.ToString(), DecisionReasonCode = route.Reason,
                AfterState = $"{{\"rxNo\":\"{rx.RxNo}\",\"status\":\"{rx.Status}\",\"alerts\":{screening.Alerts.Count}}}",
            }, ct);

            var alertViews = screening.Alerts.Select(a => new AlertView(a.Kind.ToString(), a.Severity, a.Detail)).ToList();
            return Results.Created($"/api/v1/prescriptions/{rx.PrescriptionId}", PrescriptionResponse.From(rx, alertViews));
        }).RequireAuthorization(HbmpPolicies.Scope("rx:write"));

        v1.MapGet("/{id:guid}", async (Guid id, HttpRequest http, PharmacyDbContext db, PharmacyGate gate, CancellationToken ct) =>
        {
            var rx = await db.Prescriptions.AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.PrescriptionId == id, ct);
            if (rx is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            var denied = await gate.CheckAsync(PharmacyPolicies.RxRead, "prescription", id.ToString(), rx.BeneficiaryId, http.Headers.Authorization.ToString(), ct);
            if (denied is not null) return denied;
            return Results.Ok(PrescriptionResponse.From(rx));
        });

        // My prescriptions (prescriber's worklist, US-033) — the e-prescriptions I authored, newest first,
        // scoped by CreatedBy == subject. No treating-gate needed (you always relate to what you prescribed).
        v1.MapGet("/mine", async (string? status, PharmacyDbContext db, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var sub = me.Principal?.Subject;
            if (string.IsNullOrWhiteSpace(sub)) return Results.Ok(Array.Empty<PrescriptionResponse>());
            var q = db.Prescriptions.AsNoTracking().Include(p => p.Lines).Where(p => p.CreatedBy == sub);
            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<RxStatus>(status, ignoreCase: true, out var st))
                q = q.Where(p => p.Status == st);
            var rows = await q.OrderByDescending(p => p.SubmittedAt).Take(100).ToListAsync(ct);
            return Results.Ok(rows.Select(p => PrescriptionResponse.From(p)));
        }).RequireAuthorization(HbmpPolicies.Scope("pharmacy:read"));

        v1.MapPost("/{id:guid}/cancel", async (
            Guid id, CancelRequest req, HttpRequest http, PharmacyDbContext db, PharmacyGate gate,
            IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var rx = await db.Prescriptions.Include(p => p.Lines).FirstOrDefaultAsync(p => p.PrescriptionId == id, ct);
            if (rx is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            var denied = await gate.CheckAsync(PharmacyPolicies.RxCreate, "prescription", id.ToString(), rx.BeneficiaryId, http.Headers.Authorization.ToString(), ct);
            if (denied is not null) return denied;

            if (!PrescriptionWorkflow.CanCancel(rx.Status))
                return Results.Problem(statusCode: 409, title: "transition-denied", type: "urn:hbmp:transition-denied",
                    detail: $"A prescription in status {rx.Status} cannot be cancelled.");

            rx.Status = RxStatus.Cancelled;
            foreach (var l in rx.Lines.Where(l => l.Status == RxLineStatus.Active)) l.Status = RxLineStatus.Cancelled;
            await db.SaveChangesAsync(ct);
            await outbox.EnqueueAsync("RxCancelled", "pharmacy.events", new { tenantId = rx.TenantId, prescriptionId = rx.PrescriptionId, reason = req.Reason }, ct);
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "prescription", EntityId = rx.PrescriptionId.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "Cancelled", DecisionReasonCode = req.Reason,
            }, ct);
            return Results.Ok(PrescriptionResponse.From(rx));
        }).RequireAuthorization(HbmpPolicies.Scope("rx:write"));
    }
}
