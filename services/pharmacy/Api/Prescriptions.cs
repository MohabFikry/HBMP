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

        /*
         * 29.5 — THE REFILL-FREQUENCY MASTER TABLE (design 45 §5).
         *
         * Supervisor-configurable, which is the whole reason it is a table rather than an enum: adding
         * "every 6 months" must be a DATA change, not a release. That is only true if something can read
         * it — the table was seeded and administered and nothing ever exposed it, so the composer had no
         * vocabulary to offer and a chronic script could not be written at all.
         *
         * INACTIVE rows are excluded. Offering one would let a doctor compose a script the write path then
         * refuses with `unknown-refill-frequency`, and a composer that knows a vocabulary the server
         * rejects produces failures nobody can explain from the screen.
         */
        app.MapGet("/api/v1/refill-frequencies", async (PharmacyDbContext db, CancellationToken ct) =>
            Results.Ok(await db.RefillFrequencies.AsNoTracking()
                .Where(x => x.IsActive)
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Code)
                .Select(x => new RefillFrequencyView(x.Code, x.Months, x.NameEn, x.NameAr))
                .ToListAsync(ct)))
            .RequireAuthorization(HbmpPolicies.Scope("rx:write"))
            .Produces<IEnumerable<RefillFrequencyView>>();

        /*
         * 29.5 — THE SCHEDULE PREVIEW (design 45 §5): "show the computed window schedule with per-window
         * quantities BEFORE submit, so the doctor sees 34/33/33 and can adjust".
         *
         * COMPUTED HERE, NOT IN THE CLIENT. Re-implementing largest-remainder in TypeScript would fork the
         * one piece of arithmetic in this phase that must not be forked. The two copies would drift, and
         * the drift would surface as a doctor being shown a schedule the pharmacy never honours — with the
         * screen and the database each able to cite their own correct-looking numbers. So the preview calls
         * exactly what the write path calls, and a divergence becomes impossible rather than unlikely.
         *
         * It is a POST because it carries a body, and it writes NOTHING: no prescription, no window, no
         * audit of a PHI read, because it reads no patient data. The inputs are the drug's pack facts and
         * the doctor's own numbers.
         */
        v1.MapPost("/chronic-preview", async (
            ChronicPreviewRequest req, HttpRequest http, PharmacyDbContext db, IDrugValidator drugs,
            IBusinessCalendar calendar, CancellationToken ct) =>
        {
            // The SAME refusals as submit, in the same order, so the preview can never be more permissive
            // than the thing it previews. A preview that accepted what the write path rejects is worse
            // than no preview: it tells the doctor it will work.
            if (!ChronicAllocation.IsChronicDuration(req.DurationDays))
                return Results.Problem(statusCode: 422, title: "not-chronic", type: "urn:hbmp:not-chronic",
                    detail: "A chronic prescription needs a duration of more than one month. A 14-day "
                          + "course is not chronic — write it as acute.");

            var frequency = await db.RefillFrequencies.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Code == req.RefillFrequencyCode && f.IsActive, ct);
            if (frequency is null)
                return Results.Problem(statusCode: 422, title: "unknown-refill-frequency",
                    type: "urn:hbmp:unknown-refill-frequency",
                    detail: "A chronic prescription needs an ACTIVE refill frequency.");

            /*
             * THE PACK FACTS ARE MASTER DATA, SO THIS READS THEM (design 45 §6).
             *
             * They are NOT the composer's to supply. A screen that had to fetch a drug's pack size and hand
             * it back would be a second place deciding what the catalogue says, and the version that drifted
             * would be the one the doctor was shown. The same `PackAsync` the write path calls is called
             * here, so the preview and the prescription agree by construction.
             *
             * An explicitly-supplied value still wins, for callers that genuinely hold the facts.
             */
            var pack = req.DrugId is { } drugId
                ? await drugs.PackAsync(drugId, http.Headers.Authorization.ToString(), ct)
                : null;

            var plan = ChronicAllocation.Plan(new AllocationRequest(
                DosePerAdministration: req.DoseAmount ?? 1,
                TimesPerDay: req.TimesPerDay ?? 1,
                DurationDays: req.DurationDays,
                FrequencyMonths: frequency.Months,
                IsPackSplittable: req.IsPackSplittable ?? pack?.IsPackSplittable,
                // 31.5 — what the box HOLDS, like every other quantity since 31.3.
                PackContent: req.PackContent ?? pack?.PackContent));

            // ABSENCE OF DATA IS NEVER A CLEAN RESULT (invariant 8). The missing field is NAMED, because
            // "could not compute" on its own sends a prescriber to guess, and a silently wrong quantity is
            // a dispensing error. Never a zero, never a default.
            if (plan.NotChecked)
                return Results.Problem(statusCode: 422, title: "quantity-not-checked",
                    type: "urn:hbmp:quantity-not-checked",
                    detail: $"Master data does not record '{plan.MissingField}' for this drug, so its refill "
                          + "quantities cannot be computed. A silently wrong quantity is a dispensing error.");

            var windows = WindowSchedule.Build(
                plan.Windows, calendar.Today(), frequency.Months, req.DurationDays, EarlyToleranceDays);

            return Results.Ok(new ChronicPreviewView(
                plan.Total,
                plan.Unit.ToString(),
                frequency.Months,
                [.. windows.Select(w => new ChronicWindowView(
                    w.WindowNo,
                    w.ScheduledOpen.ToString("yyyy-MM-dd"),
                    w.OpensAt.ToString("yyyy-MM-dd"),
                    w.ClosesAt.ToString("yyyy-MM-dd"),
                    w.AllocatedQuantity))]));
        })
        .RequireAuthorization(HbmpPolicies.Scope("rx:write"))
        .Produces<ChronicPreviewView>();

        /*
         * 29.6 — HOW MUCH WILL BE DISPENSED, before the doctor commits (design 45 §6).
         *
         * The composer fills its quantity field in from this rather than multiplying three numbers of its
         * own. `QuantityMath` is the one implementation of that arithmetic — the write path grades against
         * it, the dispensing counter meters against it, and a TypeScript copy in the browser would be a
         * second answer to "how much medicine does this person get".
         *
         * The pack facts are MASTER DATA and are read HERE, from the same `PackAsync` the write path calls.
         * A screen that fetched a drug's pack size and handed it back would be a second place deciding what
         * the catalogue says, and the version that drifted would be the one on screen. That is not a
         * hypothetical: it is the defect the chronic preview above shipped with.
         *
         * Writes nothing, reads no patient data. The inputs are the drug and the doctor's own numbers.
         */
        v1.MapPost("/quantity-preview", async (
            QuantityPreviewRequest req, HttpRequest http, IDrugValidator drugs, CancellationToken ct) =>
        {
            var pack = req.DrugId is { } drugId
                ? await drugs.PackAsync(drugId, http.Headers.Authorization.ToString(), ct)
                : null;

            /*
             * 31.3 — divided by what the box HOLDS, not by the catalogue's pack size.
             *
             * `pack_size` counts the catalogue's minor units, which is the same thing the dose counts only
             * for tablets and their kin. A box of five insulin pens is `pack_size = 5` and holds 1500 IU; a
             * 120 ml bottle of syrup is `pack_size = 1`. `pack_content` is the number in the unit the dose is
             * written in, so one division answers every form.
             */
            var outcome = QuantityMath.Compute(
                req.DoseAmount, req.TimesPerDay, req.DurationDays,
                req.IsPackSplittable ?? pack?.IsPackSplittable,
                req.PackContent ?? pack?.PackContent);

            // ABSENCE IS NEVER A CLEAN RESULT (invariant 8). The missing field is NAMED — "could not
            // compute" on its own sends a prescriber to guess, and a guessed quantity is a dispensing error
            // that looks exactly like a correct one. Never a zero, never a default.
            if (outcome.Plan is not { } plan)
                return Results.Problem(statusCode: 422, title: "quantity-not-checked",
                    type: "urn:hbmp:quantity-not-checked",
                    detail: $"'{outcome.MissingField}' is not recorded for this drug, so the quantity to "
                          + "dispense cannot be computed. A silently wrong quantity is a dispensing error.");

            return Results.Ok(new QuantityPreviewView(
                plan.TotalUnits,
                plan.DispenseQuantity,
                plan.Packs,
                // 31.2 — what the pharmacy actually counts out. NULL where the catalogue does not record
                // what a box holds; the composer says so rather than showing a number.
                plan.Boxes,
                plan.PackContent,
                // What the number is COUNTED IN, so the composer can say "60 tabs" rather than "60".
                pack?.PrescribingUnit,
                req.IsPackSplittable ?? pack?.IsPackSplittable));
        })
        .RequireAuthorization(HbmpPolicies.Scope("rx:write"))
        .Produces<QuantityPreviewView>();

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
                        PackContent: pack?.PackContent));

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
                        // 31.3 — the unit travels with the number, snapshotted like the drug name above.
                        QuantityUnit = l.QuantityUnit,
                        // 31.5 — the numbers the checks above were RUN ON, kept rather than discarded once
                        // they had been used. `Dose` is the sentence they were formatted into.
                        DoseAmount = l.DoseAmount, TimesPerDay = l.TimesPerDay,
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
        })
        .RequireAuthorization(HbmpPolicies.Scope("rx:write"))
        /*
         * 31.5 — THE RESPONSE SHAPE IS PART OF THE CONTRACT, so it is declared.
         *
         * The drift gate compares the committed specs against the running services and had been passing
         * over every response body on this service, because a minimal API that returns `Results.Ok(x)`
         * publishes no schema for `x`. That is how 31.5 added three fields to a prescription line and the
         * gate reported "every committed spec matches" — it was comparing requests and routes only.
         *
         * Declared on the endpoints that return a TYPED contract, which is the clinical record: what a
         * prescription is, and what its lines say. Endpoints returning anonymous objects still publish
         * nothing, and that is stated in BUILD-STATUS rather than left to be discovered the same way.
         */
        .Produces<PrescriptionResponse>(StatusCodes.Status201Created);

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
        })
        .RequireAuthorization(HbmpPolicies.Scope("rx:write"))
        .Produces<ValidationResultView>();

        /*
         * 29.4 — THE PRESCRIPTION HALF OF THE SERVICE HISTORY (design 45 §4).
         *
         * Read by orders-service, which composes the one service-history endpoint the modal calls. It is
         * declared BEFORE `/{id:guid}` so the literal segment is unambiguous.
         *
         * GATED HERE, on the CALLER's token. orders-service forwards the bearer rather than acting as
         * itself, so this is the same treating-relationship question pharmacy asks of any other read of a
         * patient's medication record — an aggregating caller does not widen what its user may see.
         *
         * MIN-NECESSARY: what a prescriber needs to answer "has this patient had this medicine before?" —
         * the drug, when, and what became of it. No dose, no diagnosis, no cost, no prescriber notes.
         */
        v1.MapGet("/history/{beneficiaryId:guid}", async (
            Guid beneficiaryId, string? code, HttpRequest http, PharmacyDbContext db, PharmacyGate gate,
            IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var bearer = http.Headers.Authorization.ToString();
            var denied = await gate.CheckAsync(PharmacyPolicies.RxRead, "prescription", null, beneficiaryId, bearer, ct);
            if (denied is not null) return denied;

            var q = db.Prescriptions.AsNoTracking().Include(p => p.Lines)
                .Where(p => p.BeneficiaryId == beneficiaryId);

            var trimmed = code?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed) && Guid.TryParse(trimmed, out var drugId))
                q = q.Where(p => p.Lines.Any(l => l.DrugId == drugId));

            var rows = await q.OrderByDescending(p => p.SubmittedAt).Take(200).ToListAsync(ct);

            var items = rows
                .Where(p => p.SubmittedAt is not null)
                .SelectMany(p => p.Lines
                    .Where(l => string.IsNullOrWhiteSpace(trimmed)
                                || !Guid.TryParse(trimmed, out var d)
                                || l.DrugId == d)
                    .Select(l => new
                    {
                        prescriptionId = p.PrescriptionId,
                        rxNo = p.RxNo,
                        prescriptionLineId = l.PrescriptionLineId,
                        drugId = l.DrugId,
                        drugName = l.DrugName,
                        // When the prescription was written. SubmittedAt is null on a draft, which never
                        // reaches this history: a draft is not something the patient 'had'.
                        occurredAt = p.SubmittedAt,
                        status = l.Status.ToString(),
                        prescriberId = p.PrescriberId == Guid.Empty ? null : p.PrescriberId.ToString(),
                        branchId = (string?)null,
                    }))
                .ToList();

            // Reading another service's copy of a patient's medication list is a PHI read here as much as
            // anywhere else, and it is audited HERE — where the data actually left.
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "prescription_history",
                EntityId = $"{beneficiaryId}/{trimmed ?? "*"}",
                Action = AuditAction.Read, ActorUserId = me.Principal?.Subject,
                DecisionOutcome = "Allow", DecisionReasonCode = $"rx-history:{items.Count}",
                FieldClasses = ["phi"],
            }, ct);

            return Results.Ok(new RxHistoryView(items));
        })
        .RequireAuthorization(HbmpPolicies.Scope("rx:read"))
        .Produces<RxHistoryView>();

        v1.MapGet("/{id:guid}", async (Guid id, HttpRequest http, PharmacyDbContext db, PharmacyGate gate, CancellationToken ct) =>
        {
            var rx = await db.Prescriptions.AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.PrescriptionId == id, ct);
            if (rx is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            var denied = await gate.CheckAsync(PharmacyPolicies.RxRead, "prescription", id.ToString(), rx.BeneficiaryId, http.Headers.Authorization.ToString(), ct);
            if (denied is not null) return denied;
            return Results.Ok(PrescriptionResponse.From(rx));
        }).Produces<PrescriptionResponse>();

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
        })
        .RequireAuthorization(HbmpPolicies.Scope("rx:read"))
        .Produces<IEnumerable<PrescriptionResponse>>();

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
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "Cancelled",
                // The CODE when the caller sent one — the free text is a sentence, and a sentence cannot be
                // grouped by. Falls back to the text so an older caller still records something.
                DecisionReasonCode = req.ReasonCode ?? req.Reason,
            }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(PrescriptionResponse.From(rx));
        }).RequireAuthorization(HbmpPolicies.Scope("rx:write"))
        .Produces<PrescriptionResponse>();
    }
}
