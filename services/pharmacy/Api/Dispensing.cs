using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Events;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Api;

/// <summary>Phase 6 pharmacist dispensing surface: a min-necessary SEARCH for dispensable prescriptions (US-050),
/// the ATOMIC/IDEMPOTENT/DUPLICATE-PROOF DISPENSE with batch/expiry + policy-approved substitution (US-051/US-052,
/// the medication analogue of phase-5 consume), and the OUT-OF-STOCK flag that never consumes the line. A pharmacist
/// never sees investigation results — this service does not expose them and the pharmacy policy bundle grants no
/// orders/result action. Every PHI read + dispense is audited.</summary>
public static class DispensingEndpoints
{
    public static void MapDispensing(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1/prescriptions").RequireAuthorization();

        // ---- 6.1 Queue: browse ALL currently-dispensable prescriptions (min-necessary projection). Same
        // dispensing-relevant fields as search, without requiring a patient identifier — the pharmacist's worklist. ----
        v1.MapGet("/queue", async (
            PharmacyDbContext db, DispensingGate gate, IAuditClient audit, IHbmpPrincipalAccessor me,
            TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.AuthorizeSearchAsync(ct);
            if (denied is not null) return denied;

            var items = await Dispensable(db, clock.GetUtcNow()).OrderBy(p => p.SubmittedAt).Take(100).ToListAsync(ct);
            await AuditRead(audit, me, "queue", items.Count);
            return Results.Ok(items.Select(DispensableRxView.From));
        }).RequireAuthorization(HbmpPolicies.Scope("pharmacy:read"));

        // ---- 6.1 Search: only dispensable prescriptions, projected to dispensing-relevant fields ----
        v1.MapGet("/search", async (
            PharmacyDbContext db, DispensingGate gate, IBeneficiaryResolver resolver, IAuditClient audit,
            IHbmpPrincipalAccessor me, HttpRequest http, TimeProvider clock,
            string? rxNo, string? patientIdentifier, string? policyNo, string? passport, string? memberNo,
            CancellationToken ct) =>
        {
            var denied = await gate.AuthorizeSearchAsync(ct);
            if (denied is not null) return denied;

            if (string.IsNullOrWhiteSpace(rxNo) && string.IsNullOrWhiteSpace(patientIdentifier) &&
                string.IsNullOrWhiteSpace(policyNo) && string.IsNullOrWhiteSpace(passport) && string.IsNullOrWhiteSpace(memberNo))
                return Results.Problem(statusCode: 400, title: "search requires rxNo, patientIdentifier, policyNo, passport or memberNo",
                    type: "urn:hbmp:search-criteria-required");

            var q = Dispensable(db, clock.GetUtcNow());

            if (!string.IsNullOrWhiteSpace(rxNo))
                q = q.Where(p => p.RxNo == rxNo);
            else if (!string.IsNullOrWhiteSpace(patientIdentifier) && Guid.TryParse(patientIdentifier, out var ben))
                q = q.Where(p => p.BeneficiaryId == ben);
            else if (!string.IsNullOrWhiteSpace(patientIdentifier))
                return Results.Ok(Array.Empty<DispensableRxView>());   // unknown identifier form → nothing
            else
            {
                // policy / passport / member number → resolve to a beneficiary via patient-service (fail-safe).
                var resolved = await resolver.ResolveAsync(policyNo, passport, memberNo, http.Headers.Authorization.ToString(), ct);
                if (resolved is null) return Results.Ok(Array.Empty<DispensableRxView>());
                q = q.Where(p => p.BeneficiaryId == resolved.Value);
            }

            var items = await q.OrderBy(p => p.SubmittedAt).Take(100).ToListAsync(ct);
            await AuditRead(audit, me, "search", items.Count);
            return Results.Ok(items.Select(DispensableRxView.From));
        }).RequireAuthorization(HbmpPolicies.Scope("pharmacy:read"));

        // ---- 6.1 Open one prescription for dispensing — enforces the reject rule with a clear reason ----
        v1.MapGet("/{id:guid}/dispensing", async (
            Guid id, PharmacyDbContext db, DispensingGate gate, IAuditClient audit, IHbmpPrincipalAccessor me,
            TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.AuthorizeSearchAsync(ct);
            if (denied is not null) return denied;

            var rx = await db.Prescriptions.AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.PrescriptionId == id, ct);
            if (rx is null) return Results.NotFound();

            var reject = RejectReason(rx, clock.GetUtcNow());
            if (reject is not null)
            {
                await audit.EmitAsync(new AuditEventDraft
                {
                    EntityType = "prescription", EntityId = id.ToString(), Action = AuditAction.Read,
                    ActorUserId = me.Principal?.Subject, DecisionOutcome = "Reject", DecisionReasonCode = reject,
                }, ct);
                return Results.Problem(statusCode: 409, title: "not-dispensable", type: "urn:hbmp:rx-not-dispensable",
                    detail: reject);
            }

            await AuditRead(audit, me, "open", rx.Lines.Count);
            return Results.Ok(DispensableRxView.From(rx));
        }).RequireAuthorization(HbmpPolicies.Scope("pharmacy:read"));

        // ---- 6.2 + 6.3 Dispense a line: atomic + idempotent + no-reuse, with batch/expiry + approved substitution ----
        v1.MapPost("/{rxId:guid}/lines/{lineId:guid}/dispense", async (
            Guid rxId, Guid lineId, DispenseRequest req, HttpRequest http, PharmacyDbContext db, DispenseExecutor executor,
            DispensingGate gate, IFormularyService formulary, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me,
            TimeProvider clock, CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required", type: "urn:hbmp:idempotency-required");
            if (req.Quantity <= 0)
                return Results.Problem(statusCode: 400, title: "invalid-quantity", type: "urn:hbmp:invalid-quantity",
                    detail: "Dispense quantity must be greater than zero.");
            if (string.IsNullOrWhiteSpace(req.BatchNo))
                return Results.Problem(statusCode: 400, title: "batch-required", type: "urn:hbmp:batch-required",
                    detail: "A batch/lot number is required on every dispense.");

            var denied = await gate.AuthorizeDispenseAsync(ct);
            if (denied is not null) return denied;

            var lineHead = await db.PrescriptionLines.AsNoTracking()
                .Where(l => l.PrescriptionLineId == lineId && l.PrescriptionId == rxId)
                .Select(l => new { l.DrugId }).FirstOrDefaultAsync(ct);
            if (lineHead is null) return Results.NotFound();

            var bearer = http.Headers.Authorization.ToString();

            // 6.3 Substitution: allowed ONLY with a policy-approved alternative; else route to approvals (never off-list).
            if (req.SubstitutedDrugId is { } sub && sub != lineHead.DrugId)
            {
                var approved = await formulary.ApprovedAlternativesAsync(lineHead.DrugId, bearer, ct);
                if (!SubstitutionPolicy.IsApproved(lineHead.DrugId, sub, approved))
                {
                    await outbox.EnqueueAsync("RxSubstitutionRoutedToApproval", "pharmacy.events",
                        new { prescriptionId = rxId, prescriptionLineId = lineId, prescribedDrugId = lineHead.DrugId, requestedDrugId = sub }, ct);
                    await audit.EmitAsync(new AuditEventDraft
                    {
                        EntityType = "prescription_line", EntityId = lineId.ToString(), Action = AuditAction.Decision,
                        ActorUserId = me.Principal?.Subject, DecisionOutcome = "SubstitutionBlocked",
                        DecisionReasonCode = $"drug:{lineHead.DrugId};requested:{sub};not-in-approved-alternatives",
                    }, ct);
                    return Results.Problem(statusCode: 409, title: "substitution-not-approved", type: "urn:hbmp:substitution-not-approved",
                        detail: "The requested drug is not a policy-approved alternative; the substitution has been routed to approvals.");
                }
            }

            var pharmacy = Guid.TryParse(me.Principal?.ProviderId, out var pg) ? pg : Guid.Empty;
            var actor = Guid.TryParse(me.Principal?.Subject, out var ag) ? ag : Guid.Empty;

            var result = await executor.DispenseAsync(rxId, lineId, idem, pharmacy, actor,
                req.Quantity, req.BatchNo, req.ExpiryDate, req.SubstitutedDrugId, req.SubstitutionReason, clock.GetUtcNow(),
                insideTransaction: async (rx, evt, c) =>
                {
                    await outbox.EnqueueAsync("RxLinesDispensed", "pharmacy.events",
                        new { prescriptionId = rxId, prescriptionLineId = lineId, evt.Quantity, evt.BatchNo, idempotencyKey = idem }, c);
                    if (rx.Status == RxStatus.Dispensed)
                        await outbox.EnqueueAsync("RxDispensed", "pharmacy.events", new { prescriptionId = rxId, rx.RxNo }, c);
                }, ct);

            switch (result.Outcome)
            {
                case DispenseOutcome.Applied:
                    await audit.EmitAsync(new AuditEventDraft
                    {
                        EntityType = "dispense_event", EntityId = result.Event!.DispenseId.ToString(), Action = AuditAction.StateChange,
                        ActorUserId = me.Principal?.Subject, DecisionOutcome = "Dispensed",
                        DecisionReasonCode = $"rx:{rxId};line:{lineId};qty:{req.Quantity};batch:{req.BatchNo};expiry:{req.ExpiryDate};" +
                                             $"sub:{req.SubstitutedDrugId?.ToString() ?? "-"};key:{idem}",
                    }, ct);
                    return Results.Ok(DispenseResponse.From(result.Prescription!, result.Event!, replayed: false));

                case DispenseOutcome.Replayed:
                    return Results.Ok(DispenseResponse.From(result.Prescription!, result.Event!, replayed: true));

                case DispenseOutcome.NotFound:
                    return Results.NotFound();
                case DispenseOutcome.LineNotFound:
                    return Results.Problem(statusCode: 404, title: "line-not-found", type: "urn:hbmp:line-not-found",
                        detail: "No such prescription line on this prescription.");
                case DispenseOutcome.InvalidQuantity:
                    return Results.Problem(statusCode: 400, title: "invalid-quantity", type: "urn:hbmp:invalid-quantity",
                        detail: "Dispense quantity must be greater than zero.");
                case DispenseOutcome.AlreadyDispensed:
                    return Results.Problem(statusCode: 409, title: "already-dispensed", type: "urn:hbmp:line-already-dispensed",
                        detail: "This line has already been fully dispensed and cannot be dispensed again.");
                case DispenseOutcome.OverDispense:
                    return Results.Problem(statusCode: 422, title: "over-dispense", type: "urn:hbmp:over-dispense",
                        detail: "Requested quantity exceeds the remaining quantity on the line.");
                case DispenseOutcome.RxNotDispensable:
                    return Results.Problem(statusCode: 409, title: "not-dispensable", type: "urn:hbmp:rx-not-dispensable",
                        detail: "This prescription is expired, cancelled, rejected, or already fully dispensed.");
                case DispenseOutcome.ExpiredLot:
                    return Results.Problem(statusCode: 422, title: "expired-lot", type: "urn:hbmp:expired-lot",
                        detail: "The batch/lot expiry date is in the past; expired stock cannot be dispensed.");
                case DispenseOutcome.Conflict:
                    return Results.Problem(statusCode: 409, title: "concurrent-dispense", type: "urn:hbmp:concurrent-dispense",
                        detail: "The line was concurrently dispensed by another request; re-read and retry.");
                default:
                    return Results.Problem(statusCode: 400, title: "invalid-dispense");
            }
        }).RequireAuthorization(HbmpPolicies.Scope("pharmacy:dispense"));

        // ---- 6.3 Out-of-stock: flag WITHOUT consuming; the unfilled quantity stays available; notify prescriber/beneficiary ----
        v1.MapPost("/{rxId:guid}/lines/{lineId:guid}/out-of-stock", async (
            Guid rxId, Guid lineId, OutOfStockRequest req, PharmacyDbContext db, DispensingGate gate,
            IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var denied = await gate.AuthorizeDispenseAsync(ct);
            if (denied is not null) return denied;

            var rx = await db.Prescriptions.AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.PrescriptionId == rxId, ct);
            if (rx is null) return Results.NotFound();
            var line = rx.Lines.FirstOrDefault(l => l.PrescriptionLineId == lineId);
            if (line is null) return Results.NotFound();

            // No accumulator change — the line stays available for backorder / a later visit. Notify + audit only.
            await outbox.EnqueueAsync("RxLineOutOfStock", "pharmacy.events", new
            {
                prescriptionId = rxId, prescriptionLineId = lineId, beneficiaryId = rx.BeneficiaryId,
                prescriberId = rx.PrescriberId, drugId = line.DrugId, quantity = req.Quantity ?? line.QuantityRemaining,
                note = req.Note,
            }, ct);
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "prescription_line", EntityId = lineId.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "OutOfStock",
                DecisionReasonCode = $"rx:{rxId};line:{lineId};qty:{req.Quantity?.ToString() ?? "remaining"};note:{req.Note}",
            }, ct);
            return Results.Accepted($"/api/v1/prescriptions/{rxId}/dispensing",
                new { flagged = true, prescriptionLineId = lineId, remaining = line.QuantityRemaining });
        }).RequireAuthorization(HbmpPolicies.Scope("pharmacy:dispense"));
    }

    /// <summary>Dispensable prescriptions the pharmacist may act on: status Approved/PartiallyDispensed, within the
    /// validity window, with ≥1 line that still has remaining quantity. Diagnoses/notes are never stored here.</summary>
    private static IQueryable<Prescription> Dispensable(PharmacyDbContext db, DateTimeOffset now) =>
        db.Prescriptions.AsNoTracking().Include(p => p.Lines)
            .Where(p => p.Status == RxStatus.Approved || p.Status == RxStatus.PartiallyDispensed)
            .Where(p => p.ExpiresAt == null || p.ExpiresAt > now)
            .Where(p => p.Lines.Any(l => (l.Status == RxLineStatus.Active || l.Status == RxLineStatus.PartiallyDispensed)
                                         && l.QuantityDispensed < l.QuantityPrescribed));

    /// <summary>The Rx reject rule (23 §3): a clear reason when a prescription may not be opened/dispensed, else null.</summary>
    private static string? RejectReason(Prescription rx, DateTimeOffset now)
    {
        if (rx.Status == RxStatus.Expired || (rx.ExpiresAt is { } exp && exp <= now)) return "The prescription has expired.";
        if (rx.Status == RxStatus.Cancelled) return "The prescription has been cancelled.";
        if (rx.Status == RxStatus.Rejected) return "The prescription was rejected.";
        if (rx.Status == RxStatus.Dispensed) return "The prescription has already been fully dispensed.";
        if (!PrescriptionWorkflow.IsDispensable(rx.Status)) return $"A prescription in status {rx.Status} is not dispensable.";
        return null;
    }

    private static async Task AuditRead(IAuditClient audit, IHbmpPrincipalAccessor me, string op, int count) =>
        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "prescription", EntityId = op, Action = AuditAction.Read,
            ActorUserId = me.Principal?.Subject, DecisionOutcome = "Allow",
            DecisionReasonCode = $"pharmacist-{op}:{count}", FieldClasses = ["phi"],
        });
}
