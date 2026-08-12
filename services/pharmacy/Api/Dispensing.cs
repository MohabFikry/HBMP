using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Events;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Mersal.Time;
using Mersal.BeneficiaryLookup;

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
            TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var denied = await gate.AuthorizeSearchAsync(ct);
            if (denied is not null) return denied;

            var queueNow = clock.GetUtcNow();
            var items = await Dispensable(db, queueNow).OrderBy(p => p.SubmittedAt).Take(100).ToListAsync(ct);
            await AuditRead(audit, me, "queue", [.. items.Select(p => p.PrescriptionId)]);
            return Results.Ok(items.Select(p => DispensableRxView.From(p, queueNow)));
        })
        .RequireAuthorization(HbmpPolicies.Scope("pharmacy:read"))
        // 31.5 — the response shape is contract. See Prescriptions.cs for why these are declared at all.
        .Produces<IEnumerable<DispensableRxView>>();

        // ---- 6.1 Search: only dispensable prescriptions, projected to dispensing-relevant fields ----
        v1.MapGet("/search", async (
            PharmacyDbContext db, DispensingGate gate, IBeneficiaryResolver resolver, IAuditClient audit,
            IHbmpPrincipalAccessor me, HttpRequest http, TimeProvider clock,
            string? rxNo, string? patientIdentifier, string? cardNumber, string? passport, string? memberNo,
            CancellationToken ct) =>
        {
            var denied = await gate.AuthorizeSearchAsync(ct);
            if (denied is not null) return denied;

            if (string.IsNullOrWhiteSpace(rxNo) && string.IsNullOrWhiteSpace(patientIdentifier) &&
                string.IsNullOrWhiteSpace(cardNumber) && string.IsNullOrWhiteSpace(passport) && string.IsNullOrWhiteSpace(memberNo))
                return Results.Problem(statusCode: 400, title: "search requires rxNo, patientIdentifier, cardNumber, passport or memberNo",
                    type: "urn:hbmp:search-criteria-required");

            // Expired prescriptions are INCLUDED here — see Outstanding(). The counter must be able to
            // see that a lapsed prescription exists in order to request an extension against it.
            var now = clock.GetUtcNow();
            var q = Outstanding(db);

            if (!string.IsNullOrWhiteSpace(rxNo))
                q = q.Where(p => p.RxNo == rxNo);
            else if (!string.IsNullOrWhiteSpace(patientIdentifier) && Guid.TryParse(patientIdentifier, out var ben))
                q = q.Where(p => p.BeneficiaryId == ben);
            else if (!string.IsNullOrWhiteSpace(patientIdentifier))
                return Results.Ok(Array.Empty<DispensableRxView>());   // unknown identifier form → nothing
            else
            {
                // card / passport / member number → resolve to a beneficiary via patient-service.
                // TWO identifiers are required (doc 43 §7 D5): a card number is printed on something that
                // gets shared, photographed and reused, so one number must not open a person's record.
                //
                // Each outcome answers a different question, and only ONE of them is "no prescriptions".
                // This used to return an empty list for all of them, so a pharmacist whose token could not
                // read patient-service was told a member with three live prescriptions had none.
                var resolution = await resolver.ResolveAsync(cardNumber, passport, memberNo, http.Headers.Authorization.ToString(), ct);
                switch (resolution.Outcome)
                {
                    case ResolveOutcome.TooFewIdentifiers:
                        return Results.Problem(
                            statusCode: 422, title: "two-identifiers-required", type: "urn:hbmp:two-identifiers-required",
                            detail: "Searching by card number, passport or member number requires at least two of "
                                    + "them. A card number alone is a lookup key, not proof of identity.");
                    case ResolveOutcome.Unavailable:
                        return Results.Problem(
                            statusCode: 503, title: "patient-directory-unavailable", type: "urn:hbmp:patient-directory-unavailable",
                            detail: "The patient directory could not be reached, so these identifiers could not be "
                                    + "resolved. This is NOT a report that the member has no prescriptions.");
                    case ResolveOutcome.NotFound:
                        // A real answer: those identifiers match nobody. An empty list is correct here.
                        return Results.Ok(Array.Empty<DispensableRxView>());
                    default:
                        q = q.Where(p => p.BeneficiaryId == resolution.BeneficiaryId!.Value);
                        break;
                }
            }

            var items = await q.OrderBy(p => p.SubmittedAt).Take(100).ToListAsync(ct);
            await AuditRead(audit, me, "search", [.. items.Select(p => p.PrescriptionId)]);
            return Results.Ok(items.Select(p => DispensableRxView.From(p, now)));
        })
        .RequireAuthorization(HbmpPolicies.Scope("pharmacy:read"))
        .Produces<IEnumerable<DispensableRxView>>();

        // ---- 6.1 Open one prescription for dispensing — enforces the reject rule with a clear reason ----
        v1.MapGet("/{id:guid}/dispensing", async (
            Guid id, PharmacyDbContext db, DispensingGate gate, IAuditClient audit, IHbmpPrincipalAccessor me,
            TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var denied = await gate.AuthorizeSearchAsync(ct);
            if (denied is not null) return denied;

            var rx = await db.Prescriptions.AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.PrescriptionId == id, ct);
            if (rx is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var openNow = clock.GetUtcNow();
            var reject = RejectReason(rx, openNow);
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

            await AuditRead(audit, me, "open", [rx.PrescriptionId]);
            return Results.Ok(DispensableRxView.From(rx, openNow));
        })
        .RequireAuthorization(HbmpPolicies.Scope("pharmacy:read"))
        .Produces<DispensableRxView>();

        // ---- 6.2 + 6.3 Dispense a line: atomic + idempotent + no-reuse, with batch/expiry + approved substitution ----
        v1.MapPost("/{rxId:guid}/lines/{lineId:guid}/dispense", async (
            Guid rxId, Guid lineId, DispenseRequest req, HttpRequest http, PharmacyDbContext db, DispenseExecutor executor,
            DispensingGate gate, IFormularyService formulary, IClinicalValidationPorts catalogue,
            IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me,
            TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
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
                .Select(l => new { l.DrugId, l.TenantId }).FirstOrDefaultAsync(ct);
            if (lineHead is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var bearer = http.Headers.Authorization.ToString();

            // 6.3 Substitution: allowed ONLY with a policy-approved alternative; else route to approvals (never off-list).
            if (req.SubstitutedDrugId is { } sub && sub != lineHead.DrugId)
            {
                var approved = await formulary.ApprovedAlternativesAsync(lineHead.DrugId, bearer, ct);
                if (!SubstitutionPolicy.IsApproved(lineHead.DrugId, sub, approved))
                {
                    await outbox.EnqueueAsync("RxSubstitutionRoutedToApproval", "pharmacy.events",
                        new { tenantId = lineHead.TenantId, prescriptionId = rxId, prescriptionLineId = lineId, prescribedDrugId = lineHead.DrugId, requestedDrugId = sub }, ct);
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

            // The substitute's catalogue NAME, resolved here — OUTSIDE the dispense transaction and only when
            // there is a substitution. The authorization the counter is about to issue records what was
            // handed over, and an id alone makes the approval team look up a GUID to find out what a patient
            // received. A lookup failure leaves the label null and the id still recorded: a display string is
            // never worth failing a dispense over.
            var substituteName = req.SubstitutedDrugId is { } named && named != lineHead.DrugId
                ? (await catalogue.DrugNamesAsync([named], bearer, ct)).GetValueOrDefault(named)
                : null;

            var result = await executor.DispenseAsync(rxId, lineId, idem, pharmacy, actor,
                req.Quantity, req.BatchNo, req.ExpiryDate, req.SubstitutedDrugId, req.SubstitutionReason,
                req.Note, clock.GetUtcNow(),
                insideTransaction: async (rx, evt, c) =>
                {
                    // 18.A1: carry tenant + beneficiary + benefit category + service date so policy-service
                    // can move coverage_limit.consumed_value for PHARMACY (FR-INV-006). Additive fields only.
                    await outbox.EnqueueAsync("RxLinesDispensed", "pharmacy.events",
                        new
                        {
                            prescriptionId = rxId,
                            prescriptionLineId = lineId,
                            tenantId = rx.TenantId,
                            beneficiaryId = rx.BeneficiaryId,
                            // ADR-0031 — the visit that prescribed it, and the number the step is referenced
                            // by. The DRUG is not here and must not be: a dispense step is read by reception,
                            // and "medicine dispensed · RX-2026-000031" is the act; which medicine is care.
                            encounterId = rx.EncounterId,
                            rx.RxNo,
                            benefitCategory = "PHARMACY",
                            serviceDate = calendar.Today(),   // 18.A3 — Cairo service date
                            // 19.4 — the dispensing pharmacy, for the utilization tier split. Absent rather
                            // than guessed when the principal has no provider.
                            providerId = pharmacy == Guid.Empty ? (Guid?)null : pharmacy,
                            evt.Quantity,
                            evt.BatchNo,
                            idempotencyKey = idem,
                        }, c);
                    /*
                     * ADR-0034 — the SECOND, APPROVALS-SHAPED COPY. What is handed over at the counter is
                     * not the prescription: it is a separate authorized act, and this is what makes it one.
                     *
                     * Its own queue, not `pharmacy.events`. That transport is point-to-point and
                     * policy-service already consumes it to move the benefit accumulator; a second consumer
                     * there would COMPETE for messages, and the accumulator would silently stop advancing for
                     * every dispense approvals happened to win.
                     *
                     * Inside the transaction, through the durable outbox, so the record cannot be lost — and
                     * asynchronous, so an approvals outage can never refuse a patient their medicine.
                     *
                     * `orderedCode` is the PRESCRIBED drug and stays the prescribed drug. The substitute goes
                     * in `fulfilledCode`. The prescription is not written to on this path and has nowhere to
                     * be written to.
                     */
                    var dispensedLine = rx.Lines.FirstOrDefault(l => l.PrescriptionLineId == lineId);
                    await outbox.EnqueueAsync("FulfilmentRecorded", "approvals.fulfilments",
                        new
                        {
                            tenantId = rx.TenantId,
                            beneficiaryId = rx.BeneficiaryId,
                            providerId = pharmacy == Guid.Empty ? (Guid?)null : pharmacy,
                            encounterId = rx.EncounterId,
                            source = "Prescription",
                            sourceRef = rxId.ToString(),
                            sourceNo = rx.RxNo,
                            benefitCategory = "PHARMACY",
                            actorUserId = me.Principal?.Subject,
                            fulfilledAt = evt.DispensedAt,
                            items = new[]
                            {
                                new
                                {
                                    // The dispense id, not the Idempotency-Key: the key is the CLIENT's
                                    // choice and a second client could reuse one, whereas this row exists
                                    // exactly once per thing actually handed over.
                                    fulfilmentRef = evt.DispenseId.ToString(),
                                    sourceLineId = lineId,
                                    orderedCode = lineHead.DrugId.ToString(),
                                    orderedLabel = dispensedLine?.DrugName,
                                    fulfilledCode = (evt.SubstitutedDrugId ?? lineHead.DrugId).ToString(),
                                    fulfilledLabel = evt.SubstitutedDrugId is null ? dispensedLine?.DrugName : substituteName,
                                    quantity = evt.Quantity,
                                    substitutionReason = evt.SubstitutedDrugId is null ? null : evt.SubstitutionReason,
                                },
                            },
                        }, c);

                    if (rx.Status == RxStatus.Dispensed)
                        await outbox.EnqueueAsync("RxDispensed", "pharmacy.events", new
                        {
                            tenantId = rx.TenantId, prescriptionId = rxId, rx.RxNo,
                            /*
                             * `drugId` — WHICH drug, for the read model's drug-utilization dimension and its
                             * medication code count. It carried none, so every dispense would have counted
                             * under "unknown" and the "top medications" figure would have been one bar.
                             *
                             * The drug ID, not the ATC class the projector's field is named for: the ATC
                             * lives in masterdata-service, and resolving it here would put a cross-service
                             * call inside the dispense transaction — on the path that moves a benefit
                             * accumulator. The id is a real code for "which drug"; `DimensionLabelled` is the
                             * mechanism that puts a name to an id, and classing by ATC is a masterdata
                             * enrichment on the reporting side rather than a lookup on the dispensing path.
                             */
                            drugId = evt.SubstitutedDrugId
                                     ?? rx.Lines.FirstOrDefault(l => l.PrescriptionLineId == evt.PrescriptionLineId)?.DrugId,
                        }, c);
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
                    return Results.Ok(DispenseResponse.From(result.Prescription!, result.Event!, replayed: false, clock.GetUtcNow()));

                case DispenseOutcome.Replayed:
                    return Results.Ok(DispenseResponse.From(result.Prescription!, result.Event!, replayed: true, clock.GetUtcNow()));

                case DispenseOutcome.NotFound:
                    return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
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
                case DispenseOutcome.InvalidIdempotencyKey:
                    return Results.Problem(statusCode: 400, title: "invalid-idempotency-key", type: "urn:hbmp:invalid-idempotency-key",
                        detail: "The Idempotency-Key must be non-empty, at most 80 characters, and must not contain '::'.");
                case DispenseOutcome.IdempotencyKeyReuse:
                    return Results.Problem(statusCode: 422, title: "idempotency-key-reuse", type: "urn:hbmp:idempotency-key-reuse",
                        detail: "This Idempotency-Key was already used for a different dispense. Use a new key for a changed request.");
                case DispenseOutcome.ExpiredLot:
                    return Results.Problem(statusCode: 422, title: "expired-lot", type: "urn:hbmp:expired-lot",
                        detail: "The batch/lot expiry date is in the past; expired stock cannot be dispensed.");
                case DispenseOutcome.Conflict:
                    return Results.Problem(statusCode: 409, title: "concurrent-dispense", type: "urn:hbmp:concurrent-dispense",
                        detail: "The line was concurrently dispensed by another request; re-read and retry.");
                default:
                    return Results.Problem(statusCode: 400, title: "invalid-dispense");
            }
        }).RequireAuthorization(HbmpPolicies.Scope("pharmacy:dispense"))
        .Produces<DispenseResponse>();

        // ---- 6.3 Out-of-stock: flag WITHOUT consuming; the unfilled quantity stays available; notify prescriber/beneficiary ----
        v1.MapPost("/{rxId:guid}/lines/{lineId:guid}/out-of-stock", async (
            Guid rxId, Guid lineId, OutOfStockRequest req, PharmacyDbContext db, DispensingGate gate,
            IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock,
            CancellationToken ct) =>
        {
            var denied = await gate.AuthorizeDispenseAsync(ct);
            if (denied is not null) return denied;

            // TRACKED, not AsNoTracking — 0020 made this a write. The flag lives on the line.
            var rx = await db.Prescriptions.Include(p => p.Lines).FirstOrDefaultAsync(p => p.PrescriptionId == rxId, ct);
            if (rx is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            var line = rx.Lines.FirstOrDefault(l => l.PrescriptionLineId == lineId);
            if (line is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            /*
             * RAISING THE SAME FLAG TWICE NOTIFIES ONCE (invariant 44).
             *
             * The notification route this enqueues is ACTIONABLE and escalates to the pharmacy supervisor
             * after eight hours. Two pharmacists reporting the same empty shelf — or one pharmacist whose
             * first request timed out and who pressed the button again — would put two of those in front of
             * the prescriber, each with its own escalation timer behind it. A control whose cost grows with
             * how often the counter is short is a control the counter learns not to use.
             *
             * The replay answers with what was recorded rather than 409: nothing went wrong, and the second
             * pharmacist's screen should end up showing the flag either way.
             */
            if (line.OutOfStock)
                return Results.Accepted($"/api/v1/prescriptions/{rxId}/dispensing", new
                {
                    flagged = true, prescriptionLineId = lineId, remaining = line.QuantityRemaining,
                    replayed = true, outOfStockAt = line.OutOfStockAt, outOfStockBy = line.OutOfStockBy,
                });

            /*
             * ONE TRANSACTION, EXPLICITLY.
             *
             * The platform's usual shape is "commit the business change, THEN enqueue" (ADR-0013), which
             * accepts losing an event to a crash in the gap because the next attempt re-does both. That
             * reasoning stops working the moment the guard above exists: a crash between the flag landing
             * and `RxLineOutOfStock` being staged would leave a line marked short, a counter shown a chip,
             * and a prescriber who is never told — and the retry, finding the flag already set, would return
             * the replay and notify nobody. The failure would be permanent and silent.
             *
             * So the flag and its announcement commit together or neither does.
             */
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // The accumulator is NOT touched, in either direction. `QuantityRemaining` is unchanged and
            // `Status` is not written: the line stays dispensable, because stock arriving tomorrow must not
            // require anything to be undone. Out of stock is a fact about the pharmacy.
            line.OutOfStockAt = clock.GetUtcNow();
            line.OutOfStockBy = me.Principal?.Subject;
            line.OutOfStockQty = req.Quantity;
            line.OutOfStockNote = req.Note;

            await outbox.EnqueueAsync("RxLineOutOfStock", "pharmacy.events", new
            {
                tenantId = rx.TenantId, prescriptionId = rxId, prescriptionLineId = lineId, beneficiaryId = rx.BeneficiaryId,
                prescriberId = rx.PrescriberId, drugId = line.DrugId, quantity = req.Quantity ?? line.QuantityRemaining,
                note = req.Note,
            }, ct);
            /*
             * THE SECOND, NOTIFICATION-SHAPED COPY (audit §11.3).
             *
             * `RxLineOutOfStock` has matched `RoutingTable` by name since the routing table was written, and
             * it still notified nobody: the transport is point-to-point, so a notification consumer bound to
             * `pharmacy.events` would COMPETE with policy-service for those messages and each event would
             * reach one of them, never both. So a service that wants a notification enqueues a copy to
             * notification-service's own queue — the decision `policy.registration-enrolments` already made,
             * and the one the auth decisions follow.
             *
             * Addressed to the PRESCRIBER, who is the person who has to act: the route is actionable and
             * escalates to the pharmacy supervisor after eight hours, and an escalation on a notice nobody
             * received is a safety net with nothing under it. The field bag carries the prescription number
             * and nothing clinical — not the drug, not the note. What is out of stock is between the
             * pharmacy and the prescriber; an inbox line is read by whoever holds the device.
             */
            if (rx.PrescriberId is { } prescriber)
            {
                await outbox.EnqueueAsync("RxLineOutOfStock", "notification.domain-events", new
                {
                    tenantId = rx.TenantId,
                    entityRef = $"prescription:{rxId}",
                    fields = new { @ref = rx.RxNo },
                    recipients = new[]
                    {
                        new { userId = prescriber.ToString(), role = "ordering_doctor", locale = "ar" },
                    },
                }, ct);
            }
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "prescription_line", EntityId = lineId.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "OutOfStock",
                DecisionReasonCode = $"rx:{rxId};line:{lineId};qty:{req.Quantity?.ToString() ?? "remaining"};note:{req.Note}",
            }, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Results.Accepted($"/api/v1/prescriptions/{rxId}/dispensing",
                new
                {
                    flagged = true, prescriptionLineId = lineId, remaining = line.QuantityRemaining,
                    replayed = false, outOfStockAt = line.OutOfStockAt, outOfStockBy = line.OutOfStockBy,
                });
        }).RequireAuthorization(HbmpPolicies.Scope("pharmacy:dispense"));
    }

    /// <summary>Dispensable prescriptions the pharmacist may act on: status Approved/PartiallyDispensed, within the
    /// validity window, with ≥1 line that still has remaining quantity. Diagnoses/notes are never stored here.</summary>
    private static IQueryable<Prescription> Dispensable(PharmacyDbContext db, DateTimeOffset now) =>
        Outstanding(db).Where(p => p.ExpiresAt == null || p.ExpiresAt > now);

    /// <summary>
    /// Everything still awaiting the counter, EXPIRED INCLUDED — what a member search must answer with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The search used to filter on the validity window, so an expired prescription came back as an empty
    /// list: the pharmacist was told this member has nothing, when in fact they have something that has
    /// lapsed and can be extended. That is the same defect as the resolver returning <c>null</c> for a
    /// permission failure — a true statement ("nothing dispensable") standing in for a false one ("nothing").
    /// The distinction matters most here, because the patient is at the counter and the recovery is a
    /// two-minute extension request rather than a wasted journey back to a doctor.
    /// </para>
    /// <para>
    /// Expired rows are RETURNED, not dispensable. <see cref="RejectReason"/> still refuses them and the
    /// domain rule in <c>Dispensing.CanDispense</c> is untouched; the view carries <c>Expired</c> so the
    /// screen says so in words.
    /// </para>
    /// </remarks>
    private static IQueryable<Prescription> Outstanding(PharmacyDbContext db) =>
        db.Prescriptions.AsNoTracking().Include(p => p.Lines)
            .Where(p => p.Status == RxStatus.Approved || p.Status == RxStatus.PartiallyDispensed
                        || p.Status == RxStatus.Expired)
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

    /// <summary>
    /// Audit a PHI read with the RECORDS it disclosed, one event each.
    /// </summary>
    /// <remarks>
    /// <para>This recorded <c>EntityId = "queue" | "search" | "open"</c> and a count. Both facts are true and
    /// neither is the one an audit exists to answer: "who has looked at RX-2026-000410?" had no answer,
    /// because no row on the chain named that prescription. The REJECT path on the same endpoint already did
    /// it properly, which is how the gap was visible at all — the same handler audits a refusal by id and a
    /// successful disclosure by the word "open".</para>
    /// <para>One event per disclosed record, matching patient-service's <c>BeneficiaryReadGuard</c> — the
    /// entity id is the queryable anchor, so a list read that collapses to one row makes every prescription
    /// on that page unsearchable. The page size caps this at 100; a work queue refreshed all day is exactly
    /// the surface where "nobody can tell who saw it" would otherwise hold.</para>
    /// <para>An EMPTY result still emits one event. A search that returned nothing is still an act — on an
    /// identifier search it confirms a person has no live prescription — and dropping it would leave the
    /// least explicable lookups as the only unrecorded ones.</para>
    /// </remarks>
    private static async Task AuditRead(
        IAuditClient audit, IHbmpPrincipalAccessor me, string op, IReadOnlyCollection<Guid> disclosed)
    {
        if (disclosed.Count == 0)
        {
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "prescription", EntityId = "(none)", Action = AuditAction.Read,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "Allow",
                DecisionReasonCode = $"pharmacist-{op}:0", FieldClasses = ["phi"],
            });
            return;
        }

        foreach (var id in disclosed)
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "prescription", EntityId = id.ToString(), Action = AuditAction.Read,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "Allow",
                DecisionReasonCode = $"pharmacist-{op}:{disclosed.Count}", FieldClasses = ["phi"],
            });
    }
}
