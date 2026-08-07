using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Pharmacy.Domain;
using Mersal.Prescribing;
using Mersal.Time;
using Mersal.Pharmacy.Infrastructure;
using Mersal.Validity;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Api;

/// <summary>Phase 4.3 e-prescription endpoints: create+submit (treating-gated, drug-validated, advisory
/// interaction/allergy alerts, config-driven approval routing) and read/cancel. Order + lines + state change are
/// written in one transaction; RxCreated then RxSubmitted (and RxApproved when auto-approved) are enqueued to the
/// outbox. Alerts are advisory (recorded, never blocking). Every mutation is audited.</summary>
public static class PrescriptionEndpoints
{
    /// <summary>Design 45 §5's default early tolerance. Held here rather than read from system_config until a
    /// supervisor surface exists to set it — a configurable value with no configured consumer is a
    /// decoration, and the number is stored ON each window so a later change never rewrites an issued one.</summary>
    private const int EarlyToleranceDays = 5;

    public static void MapPrescriptions(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1/prescriptions").RequireAuthorization();

        v1.MapPost("", async (
            CreatePrescriptionRequest req, HttpRequest http, PharmacyDbContext db, PharmacyGate gate,
            IDrugValidator drugs, IPrescribingScreener screener, RxRoutingOptions routing, SequenceIssuer seq,
            PrescriptionValidationService validation, IValidityPolicySource validity,
            IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock,
            IBusinessCalendar calendar, CancellationToken ct) =>
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

            // The NAME, not an existence bit, and the name MASTER DATA gives rather than one the client
            // sent. A client-supplied label would let the medicine printed on the dispensing screen differ
            // from the drug actually prescribed, which is the one disagreement this record must not permit.
            var drugNames = new Dictionary<Guid, string>();
            foreach (var l in req.Lines)
            {
                if (l.QuantityPrescribed <= 0 || l.RefillsAllowed < 0)
                    return Results.Problem(statusCode: 422, title: "invalid-line", type: "urn:hbmp:invalid-line",
                        detail: $"Drug '{l.DrugId}' needs quantityPrescribed > 0 and refillsAllowed ≥ 0.");
                var drugName = await drugs.DrugNameAsync(l.DrugId, bearer, ct);
                if (drugName is null)
                    return Results.Problem(statusCode: 422, title: "unknown-drug", type: "urn:hbmp:unknown-drug",
                        detail: $"Drug '{l.DrugId}' is not present in master data.");
                drugNames[l.DrugId] = drugName;
            }

            var now = clock.GetUtcNow();
            var actor = me.Principal?.Subject;
            // The PRESCRIBER is the person who wrote it — the token's SUBJECT. This read
            // `me.Principal.ProviderId`, the provider the caller belongs to, which a doctor's token does not
            // carry at all (doctors are practitioner-scoped, not provider-scoped). So the parse failed and
            // Guid.Empty was written on every prescription this platform had ever issued. Migration 0006
            // backfills the existing rows from created_by, which is this same value.
            var prescriberId = Guid.TryParse(actor, out var pid) ? pid : Guid.Empty;
            var prescriberName = me.Principal?.DisplayName;

            // ---------------------------------------------------------------- 26.4 step 2: AUTHORITATIVE
            //
            // The server re-runs the whole validation from current state. The client's step-1 findings are
            // display state and are not read here — not to decide, not to skip work, not at all. A submission
            // carrying a clean verdict for a drug the engine refuses must still be refused, or the entire
            // engine is bypassed by a crafted payload (doc 43 §5, §8 invariant 4).
            // `req.DiagnosisIcdCodes` IS NOT READ. `authoritative: true` makes the service fetch the
            // encounter's diagnoses from emr, which is the half of step 2 that phase 26 left open: it
            // re-ran every check server-side and then took its most important input from the request body,
            // so an emptied or edited diagnosis array changed what the engine concluded (doc 44 §1.3).
            var (validationResult, validationRequest, diagnosisContext) = await validation.EvaluateAsync(
                req.BeneficiaryId, req.EncounterId, req.Lines, clientDiagnoses: [], bearer, ct,
                authoritative: true);

            // What the prescription is RECORDED as having been checked against — the SERVER's list, not the
            // client's. A snapshot taken from the request body would let the stored record disagree with the
            // findings stored beside it.
            var diagnoses = diagnosisContext.IcdCodes;

            // Acknowledgement is what gates submission — not the warning. A prescriber may proceed past any
            // clinical warning with a reason; they may not proceed silently.
            var acknowledged = (req.Acknowledgements ?? [])
                .Where(a => !string.IsNullOrWhiteSpace(a.Reason))
                .Select(a => (a.ClientLineId, a.FindingKind))
                .ToHashSet();

            var unacknowledged = validationResult.Findings
                .Where(f => f.RequiresAcknowledgement)
                .Where(f => !acknowledged.Contains((f.LineId, f.Kind.ToString())))
                .ToList();

            if (unacknowledged.Count > 0)
            {
                return Results.Problem(
                    statusCode: 422, title: "unacknowledged-warning", type: "urn:hbmp:unacknowledged-warning",
                    detail: "Each warning must be acknowledged with a reason before submission: "
                            + string.Join("; ", unacknowledged.Select(f => $"{f.Kind}: {f.MessageEn}")));
            }

            // A benefit refusal is not overridable — benefit rules block, clinical checks warn.
            var blocked = validationResult.Findings.Where(f => f.IsBlocking).ToList();
            if (blocked.Count > 0)
            {
                return Results.Problem(
                    statusCode: 422, title: "blocked-by-benefit-rule", type: "urn:hbmp:benefit-blocked",
                    detail: string.Join("; ", blocked.Select(f => f.MessageEn)));
            }

            // Line identity: the client's ClientLineId is used only to correlate findings and
            // acknowledgements. The database key is minted here and never taken from the request.
            var lineIdByClientId = new Dictionary<Guid, Guid>();

            /*
             * WHEN THIS PRESCRIPTION STOPS BEING SAFE TO DISPENSE.
             *
             * The column has existed since migration 0001 and the dispensing rule has always honoured it —
             * `expires_at` was simply never written, so every prescription this platform has ever issued was
             * valid for ever. A prescription is a clinician's judgement about a patient at a moment; handing
             * one over six months later means dispensing on reasoning nobody has re-examined.
             *
             * The tenant's period is resolved from configuration, and the client's `ExpiresAt` may only make
             * it SHORTER. A prescriber writing a three-day course may say so; nobody may hand themselves a
             * longer validity than the Medical Director set by putting a date in a request body.
             */
            var validityDays = await validity.DaysAsync(ValidityArtefact.Prescription, bearer, ct);
            var policyExpiry = ValidityPolicy.ExpiryFor(now, validityDays);
            var expiresAt = req.ExpiresAt is { } requested && requested < policyExpiry ? requested : policyExpiry;

            /*
             * 30.x — ACUTE OR CHRONIC (design 45 §5), and the wiring phase 29 built the machinery for and
             * never connected.
             *
             * Everything below runs BEFORE the transaction, so a refusal writes nothing. A chronic script
             * that could not be scheduled must not become a chronic script with no windows: that is
             * undispensable in a way nothing reports, which is exactly what the migration's CHECK guards
             * against and what this endpoint must never rely on the CHECK to catch.
             */
            var chronic = string.Equals(req.Kind, "Chronic", StringComparison.OrdinalIgnoreCase);
            RefillFrequency? frequency = null;
            if (chronic)
            {
                if (req.DurationDays is not { } days || !ChronicAllocation.IsChronicDuration(days))
                    return Results.Problem(statusCode: 422, title: "not-chronic", type: "urn:hbmp:not-chronic",
                        detail: "A chronic prescription needs a duration of more than one month. A 14-day "
                              + "course is not chronic — write it as acute.");

                frequency = await db.RefillFrequencies.AsNoTracking()
                    .FirstOrDefaultAsync(f => f.Code == req.RefillFrequencyCode && f.IsActive, ct);
                if (frequency is null)
                    return Results.Problem(statusCode: 422, title: "unknown-refill-frequency",
                        type: "urn:hbmp:unknown-refill-frequency",
                        detail: "A chronic prescription needs an ACTIVE refill frequency. Without one it has "
                              + "no windows and is undispensable in a way nothing reports.");
            }
            else if (req.RefillFrequencyCode is not null)
            {
                return Results.Problem(statusCode: 422, title: "acute-has-no-schedule",
                    type: "urn:hbmp:acute-has-no-schedule",
                    detail: "An acute prescription carries no refill schedule. Allowing one would make "
                          + "\"is this chronic?\" answerable two ways.");
            }

            // The per-line allocation, computed up front so a missing pack fact refuses the whole request
            // rather than leaving some lines scheduled and others not.
            var allocations = new Dictionary<Guid, AllocationPlan>();
            if (chronic)
            {
                foreach (var l in req.Lines)
                {
                    var pack = await drugs.PackAsync(l.DrugId, bearer, ct);
                    var plan = ChronicAllocation.Plan(new AllocationRequest(
                        DosePerAdministration: l.DoseAmount ?? 1,
                        TimesPerDay: l.TimesPerDay ?? 1,
                        DurationDays: l.DurationDays ?? req.DurationDays!.Value,
                        FrequencyMonths: frequency!.Months,
                        IsPackSplittable: pack?.IsPackSplittable,
                        PackSize: pack?.PackSize));

                    // ABSENCE OF DATA IS NEVER A CLEAN RESULT. The field that is missing is named, because
                    // "could not compute" without it sends a prescriber to guess.
                    if (plan.NotChecked)
                        return Results.Problem(statusCode: 422, title: "quantity-not-checked",
                            type: "urn:hbmp:quantity-not-checked",
                            detail: $"Master data does not record '{plan.MissingField}' for one of these "
                                  + "drugs, so its refill quantities cannot be computed. A silently wrong "
                                  + "quantity is a dispensing error.");
                    allocations[l.DrugId] = plan;
                }
            }

            var rx = new Prescription
            {
                PrescriptionId = Guid.NewGuid(), RxNo = RxNo.Format(now.Year, await seq.NextAsync("rx_seq", now.Year, ct)),
                BeneficiaryId = req.BeneficiaryId, EncounterId = req.EncounterId, PrescriberId = prescriberId,
                PrescriberName = prescriberName,
                Status = RxStatus.Draft, ExpiresAt = expiresAt, IdempotencyKey = idem, CreatedBy = actor,
                PrimaryIcdCode = diagnoses.Count > 0 ? diagnoses[0] : null,
                // Snapshot, not a join: a later correction to the encounter's diagnoses must not rewrite what
                // this prescription was actually checked against.
                DiagnosisSnapshot = System.Text.Json.JsonSerializer.Serialize(diagnoses),
                // 30.x — the script's own shape. valid_from/valid_until span the WHOLE duration; the windows
                // inside it decide when each collection is due.
                Kind = chronic ? "Chronic" : "Acute",
                RefillFrequencyCode = chronic ? frequency!.Code : null,
                DurationDays = chronic ? req.DurationDays : null,
                ValidFrom = chronic ? calendar.Today() : null,
                ValidUntil = chronic ? calendar.Today().AddDays(req.DurationDays!.Value - 1) : null,
                Lines = req.Lines.Select(l =>
                {
                    var line = new PrescriptionLine
                    {
                        PrescriptionLineId = Guid.NewGuid(), DrugId = l.DrugId, DrugName = drugNames[l.DrugId],
                        Dose = l.Dose, Route = l.Route,
                        Frequency = l.Frequency, QuantityPrescribed = l.QuantityPrescribed,
                        RefillsAllowed = l.RefillsAllowed, DurationDays = l.DurationDays,
                        Status = RxLineStatus.Active,
                    };
                    // 30.x — on a chronic line the QUANTITY IS THE ALLOCATION'S TOTAL. The windows sum to it
                    // exactly, and storing a different number would make "how much was prescribed"
                    // answerable two ways.
                    if (chronic)
                    {
                        line.QuantityPrescribed = allocations[l.DrugId].Total;
                        line.DurationDays = l.DurationDays ?? req.DurationDays;
                    }
                    if (l.ClientLineId is { } cid) lineIdByClientId[cid] = line.PrescriptionLineId;
                    return line;
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

            // The authoritative run, recorded against the prescription. Append-only, and stamped Step2 so a
            // later reviewer can see what the SERVER concluded rather than what the client displayed.
            db.PrescriptionValidations.Add(validation.ToRun(
                validationResult, validationRequest, req.BeneficiaryId, rx.PrescriptionId, "Step2", actor));

            // Saved before the overrides, by hand. An override references prescription_line, and the model
            // declares no navigation between them, so EF has no graph to sort by and emits the inserts in the
            // order they were tracked — which the real foreign key then rejects. Same transaction throughout.
            await db.SaveChangesAsync(ct);

            /*
             * 30.x — THE REFILL WINDOWS, written AFTER the first save for exactly the reason the overrides
             * are: a window references prescription_line, the model declares no navigation between them, so
             * EF emits the inserts in tracking order and the real foreign key rejects them. Same transaction
             * throughout — a chronic script committed without its schedule is one a counter cannot dispense
             * and no report flags, which is the state phase 29 left the platform in.
             */
            if (chronic)
            {
                var start = calendar.Today();
                foreach (var line in rx.Lines)
                {
                    var reqLine = req.Lines.First(l => l.DrugId == line.DrugId);
                    var days = reqLine.DurationDays ?? req.DurationDays!.Value;
                    foreach (var w in WindowSchedule.Build(
                                 allocations[line.DrugId].Windows, start, frequency!.Months, days,
                                 EarlyToleranceDays))
                    {
                        db.DispenseWindows.Add(new PrescriptionDispenseWindow
                        {
                            WindowId = Guid.NewGuid(), TenantId = rx.TenantId,
                            PrescriptionId = rx.PrescriptionId, PrescriptionLineId = line.PrescriptionLineId,
                            WindowNo = w.WindowNo, ScheduledOpenDate = w.ScheduledOpen,
                            OpensAt = w.OpensAt, ClosesAt = w.ClosesAt,
                            AllocatedQuantity = w.AllocatedQuantity, DispensedQuantity = 0m,
                            Status = "Pending",
                        });
                    }
                }
            }

            // The prescriber's reasons for proceeding. Part of the record and visible to the approver.
            foreach (var ack in req.Acknowledgements ?? [])
            {
                if (string.IsNullOrWhiteSpace(ack.Reason)) continue;
                if (!lineIdByClientId.TryGetValue(ack.ClientLineId, out var lineId)) continue;
                db.PrescriptionLineOverrides.Add(new PrescriptionLineOverride
                {
                    OverrideId = Guid.NewGuid(), PrescriptionId = rx.PrescriptionId, LineId = lineId,
                    FindingKind = ack.FindingKind, Reason = ack.Reason.Trim(),
                    AcknowledgedBy = actor ?? "unknown", AcknowledgedAt = now,
                });
            }

            await db.SaveChangesAsync(ct);

            // `encounterId` — ADR-0031. The prescription has held the column since phase 4 and never put it on
            // the wire, so nothing could join "this consultation" to "these medicines". `orderedByUserId` is
            // the prescriber, so the step has a person on it rather than an empty "by".
            await outbox.EnqueueAsync("RxCreated", "pharmacy.events",
                new
                {
                    tenantId = rx.TenantId, prescriptionId = rx.PrescriptionId, rx.RxNo,
                    beneficiaryId = rx.BeneficiaryId, encounterId = rx.EncounterId,
                    orderedByUserId = rx.CreatedBy,
                }, ct);
            // `orderedByUserId` — the prescriber, carried forward for whoever ingests this into approvals, so
            // the decision notice has a human to reach. Same reason as OrderPendingApproval (§11.3), and now
            // the same VALUE: this carried `PrescriberId` (a practitioner row id) while every sibling event
            // carries the token subject, so the one field whose whole purpose is "someone to reach" named a
            // directory row rather than an account. `CreatedBy` is the person who wrote it.
            //
            // `encounterId` — ADR-0031. A gated prescription is the medication half of OrderSentForApproval,
            // and pharmacy has no other event for it: `RxCreated` fires either way and `RxApproved` fires only
            // when routing DIDN'T gate it. Without this step a prescription that went for approval looked, on
            // the episode, exactly like one that was ready to collect — and the wait it started was invisible
            // until the decision came back, which is the stretch a desk is most often asked about.
            await outbox.EnqueueAsync("RxSubmitted", "pharmacy.events",
                new
                {
                    tenantId = rx.TenantId, prescriptionId = rx.PrescriptionId, rx.RxNo,
                    beneficiaryId = rx.BeneficiaryId, encounterId = rx.EncounterId,
                    requiresApproval = route.RequiresApproval, orderedByUserId = rx.CreatedBy,
                }, ct);
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

        // ------------------------------------------------------------------ 26.4 step 1: advisory validation
        //
        // Read-shaped: it persists no draft prescription, only the record of the run. No Idempotency-Key is
        // required for that reason. Its verdict is display state and NOTHING ELSE — the submit path below
        // re-evaluates from scratch and never reads what this returned (doc 43 §5).
        v1.MapPost("/validate", async (
            ValidatePrescriptionRequest req, HttpRequest http, PharmacyDbContext db, PharmacyGate gate,
            PrescriptionValidationService validation, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            if (req.Lines is null || req.Lines.Count == 0)
                return Results.Problem(statusCode: 400, title: "nothing to validate", type: "urn:hbmp:empty-rx");

            var bearer = http.Headers.Authorization.ToString();

            // Same treating-relationship gate as writing the prescription: validation reads the
            // beneficiary's allergies and diagnoses, so it is a PHI read and is authorised as one.
            var denied = await gate.CheckAsync(PharmacyPolicies.RxCreate, "prescription", null, req.BeneficiaryId, bearer, ct);
            if (denied is not null) return denied;

            // Step 1 may use the composing screen's list for speed — it is advisory and nothing is written
            // from it. The run records the provenance as client-supplied, so a step-1/step-2 divergence has
            // an explanation on file rather than looking like an engine that changed its mind.
            var (result, request, _) = await validation.EvaluateAsync(
                req.BeneficiaryId, req.EncounterId, req.Lines, req.DiagnosisIcdCodes ?? [], bearer, ct);

            var run = validation.ToRun(result, request, req.BeneficiaryId, null, "Step1", me.Principal?.Subject);
            db.PrescriptionValidations.Add(run);
            await db.SaveChangesAsync(ct);

            return Results.Ok(PrescriptionValidationService.ToView(result, request, run.ValidationId));
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
            // `rx:read`, NOT `pharmacy:read`. This list is the PRESCRIBER's own — it filters on
            // `CreatedBy == sub` and answers "what have I written". `pharmacy:read` is the DISPENSER's scope,
            // held by pharmacists for the queue and the search; a doctor does not have it and must not need
            // it to read back their own work. The identity contract names this exact case when it introduces
            // `rx:read` ("a prescriber reading back their own prescription", IdentityContract.cs) — the scope
            // was created for this endpoint and then not applied to it.
            //
            // The effect was that every prescription a doctor submitted vanished on saving: the write
            // succeeded with 201, and the encounter's Prescriptions tab, which reads this endpoint and
            // filters it to the patient in the browser, got a 403 and rendered an empty list. A prescriber
            // had no way to see that their own prescription existed.
        }).RequireAuthorization(HbmpPolicies.Scope("rx:read"));

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

            // 24.3 — a cancelled prescription whose RxCancelled event was lost is one a pharmacy can still
            // dispense against. State change and event share one transaction.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            rx.Status = RxStatus.Cancelled;
            foreach (var l in rx.Lines.Where(l => l.Status == RxLineStatus.Active)) l.Status = RxLineStatus.Cancelled;
            await db.SaveChangesAsync(ct);
            // ADR-0031 — a cancelled prescription adds a step beside the one that wrote it; the episode is
            // what happened, so nothing is retracted. `rxNo` is the step's reference: a business key a
            // pharmacist can read back, never an internal id.
            await outbox.EnqueueAsync("RxCancelled", "pharmacy.events", new
            {
                tenantId = rx.TenantId, prescriptionId = rx.PrescriptionId, rx.RxNo,
                beneficiaryId = rx.BeneficiaryId, encounterId = rx.EncounterId,
                cancelledByUserId = me.Principal?.Subject, reason = req.Reason,
            }, ct);
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "prescription", EntityId = rx.PrescriptionId.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "Cancelled", DecisionReasonCode = req.Reason,
            }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(PrescriptionResponse.From(rx));
        }).RequireAuthorization(HbmpPolicies.Scope("rx:write"));
    }
}
