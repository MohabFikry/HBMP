using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Api;

/// <summary>Phase 4.1 clinical documentation endpoints: SOAP notes (sign-lock + addendum), diagnoses (ICD-10
/// validated), vitals (range + optional LOINC), allergies and medication history. Every read/write is gated by
/// the treating-relationship rule (US-030) through the shared authorization engine; every mutation is audited;
/// clinical codes are validated against masterdata (fail-closed). Non-clinical roles have no policy rule and are
/// default-denied. A FHIR R4 read projection is exposed alongside the canonical model.</summary>
public static class ClinicalEndpoints
{
    public static void MapClinical(this WebApplication app)
    {
        var enc = app.MapGroup("/api/v1/encounters").RequireAuthorization();
        var ben = app.MapGroup("/api/v1/beneficiaries").RequireAuthorization();

        // Min-necessary treating-relationship probe (boolean only) — the authoritative source of the treating
        // truth (emr owns encounters). orders-service / pharmacy-service call this, forwarding the caller's
        // token, to gate their own writes by the SAME rule (US-030) without duplicating encounter data.
        app.MapGet("/api/v1/treating-relationship", async (
            Guid beneficiaryId, ITreatingRelationship treating, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var p = me.Principal;
            if (p is null) return Results.Unauthorized();
            var treats = await treating.TreatsAsync(p.Subject, p.ProviderId, beneficiaryId, ct);
            return Results.Ok(new { beneficiaryId, treats });
        }).RequireAuthorization();

        // ---- My treating load (US-030) — the encounters this clinician owns (created_by = caller). This is the
        // min-necessary "my patients" worklist: a doctor sees only beneficiaries they are actively treating, never
        // the whole panel. Per-encounter clinical reads are still re-gated by the treating rule below. ----
        enc.MapGet("/mine", async (EmrDbContext db, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var p = me.Principal;
            if (p is null) return Results.Unauthorized();
            var mine = await db.Encounters.AsNoTracking()
                .Where(e => e.CreatedBy == p.Subject)
                .OrderByDescending(e => e.StartedAt)
                .Take(100)
                .ToListAsync(ct);

            // The patient's NAME on the treating clinician's own worklist.
            //
            // This list was rendering "Beneficiary •••4821" for every row, which is unusable as a worklist:
            // the doctor cannot tell which of their patients a row is without opening it. The masking is
            // right on the boards that genuinely do not need identity (lab, pharmacy, approvals); the
            // treating clinician is not one of them, and already reads the full clinical record behind each
            // of these rows.
            //
            // The source is emr's OWN `appointment.beneficiary_name`, captured at BOOKING — the same column
            // AppointmentsModule reads for the day board. No call to patient-service: emr holds no
            // beneficiary demographics, and a service fetching a sibling's data on the caller's behalf is
            // the aggregation shape this platform forbids outright.
            //
            // A walk-in encounter has no appointment and so no name here; it keeps the masked token, which
            // is the honest answer rather than a blank cell.
            var apptIds = mine.Select(e => e.AppointmentId).OfType<Guid>().Distinct().ToList();
            var nameByAppt = apptIds.Count == 0
                ? []
                : await db.Appointments.AsNoTracking()
                    .Where(a => apptIds.Contains(a.AppointmentId) && a.BeneficiaryName != null)
                    .ToDictionaryAsync(a => a.AppointmentId, a => a.BeneficiaryName, ct);

            return Results.Ok(mine.Select(e => EncounterResponse.From(
                e, e.AppointmentId is { } id ? nameByAppt.GetValueOrDefault(id) : null)));
        });

        // ---- Full clinical record (US-030) — treating clinician or approval team only ----
        enc.MapGet("/{id:guid}/clinical", async (
            Guid id, EmrDbContext db, ClinicalGate gate, HttpContext http, CancellationToken ct) =>
        {
            var enc0 = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(e => e.EncounterId == id, ct);
            if (enc0 is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var denied = await gate.CheckAsync("emr:read", EmrPolicies.Resources.Encounter, id.ToString(), enc0.BeneficiaryId, ct);
            if (denied is not null) return denied;

            var notes = await db.Notes.AsNoTracking().Where(n => n.EncounterId == id && !n.IsDeleted).ToListAsync(ct);
            var dx = await db.Diagnoses.AsNoTracking().Where(d => d.EncounterId == id && !d.IsDeleted).ToListAsync(ct);
            var vitals = await db.Vitals.AsNoTracking().Where(v => v.EncounterId == id && !v.IsDeleted).ToListAsync(ct);
            var allergies = await db.Allergies.AsNoTracking().Where(a => a.BeneficiaryId == enc0.BeneficiaryId && !a.IsDeleted).ToListAsync(ct);
            var meds = await db.MedicationHistories.AsNoTracking().Where(m => m.BeneficiaryId == enc0.BeneficiaryId && !m.IsDeleted).ToListAsync(ct);

            return Results.Ok(new ClinicalRecordResponse(
                EncounterResponse.From(enc0),
                notes.Select(NoteResponse.From).ToList(),
                dx.Select(DiagnosisResponse.From).ToList(),
                vitals.Select(VitalResponse.From).ToList(),
                allergies.Select(AllergyResponse.From).ToList(),
                meds.Select(MedicationHistoryResponse.From).ToList()));
        });

        // ---- SOAP note create (US-031) ----
        enc.MapPost("/{id:guid}/notes", async (
            Guid id, CreateNoteRequest req, EmrDbContext db, ClinicalGate gate, IAuditClient audit,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var enc0 = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(e => e.EncounterId == id, ct);
            if (enc0 is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            var denied = await gate.CheckAsync("emr:write", EmrPolicies.Resources.Note, id.ToString(), enc0.BeneficiaryId, ct);
            if (denied is not null) return denied;

            var note = new EmrNote
            {
                NoteId = Guid.NewGuid(), EncounterId = id, NoteType = req.NoteType,
                Subjective = req.Subjective, Objective = req.Objective, Assessment = req.Assessment, Plan = req.Plan,
                AuthoredBy = me.Principal!.Subject, AuthoredAt = clock.GetUtcNow(), IsSigned = false,
            };
            if (!SoapNoteRules.HasContent(note))
                return Problem(422, "empty-note", "A note must contain at least one populated section (S/O/A/P).");

            db.Notes.Add(note);
            await db.SaveChangesAsync(ct);
            await EmitAsync(audit, "emr_note", note.NoteId, AuditAction.Create, me, $"{{\"encounterId\":\"{id}\",\"type\":\"{note.NoteType}\"}}", ct);
            return Results.Created($"/api/v1/encounters/{id}/notes/{note.NoteId}", NoteResponse.From(note));
        }).RequireAuthorization(HbmpPolicies.Scope("emr:write"));

        // ---- SOAP note edit (unsigned, author only) ----
        enc.MapPut("/{id:guid}/notes/{noteId:guid}", async (
            Guid id, Guid noteId, UpdateNoteRequest req, EmrDbContext db, ClinicalGate gate, IAuditClient audit,
            IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var enc0 = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(e => e.EncounterId == id, ct);
            if (enc0 is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            var denied = await gate.CheckAsync("emr:write", EmrPolicies.Resources.Note, noteId.ToString(), enc0.BeneficiaryId, ct);
            if (denied is not null) return denied;

            var note = await db.Notes.FirstOrDefaultAsync(n => n.NoteId == noteId && n.EncounterId == id && !n.IsDeleted, ct);
            if (note is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            switch (SoapNoteRules.CanEdit(note, me.Principal!.Subject))
            {
                case NoteOutcome.AlreadySigned:
                    return Problem(409, "note-signed", "A signed note is immutable — record a correction as an addendum.");
                case NoteOutcome.NotAuthor:
                    return Problem(403, "not-author", "Only the note's author may edit it while unsigned.");
            }

            note.Subjective = req.Subjective; note.Objective = req.Objective;
            note.Assessment = req.Assessment; note.Plan = req.Plan;
            if (!SoapNoteRules.HasContent(note))
                return Problem(422, "empty-note", "A note must contain at least one populated section (S/O/A/P).");
            await db.SaveChangesAsync(ct);
            await EmitAsync(audit, "emr_note", note.NoteId, AuditAction.Update, me, "{\"edited\":true}", ct);
            return Results.Ok(NoteResponse.From(note));
        }).RequireAuthorization(HbmpPolicies.Scope("emr:write"));

        // ---- Sign a note (locks it) ----
        enc.MapPost("/{id:guid}/notes/{noteId:guid}/sign", async (
            Guid id, Guid noteId, EmrDbContext db, ClinicalGate gate, IAuditClient audit,
            CareTimelineWriter timeline, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var enc0 = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(e => e.EncounterId == id, ct);
            if (enc0 is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            var denied = await gate.CheckAsync("emr:write", EmrPolicies.Resources.Note, noteId.ToString(), enc0.BeneficiaryId, ct);
            if (denied is not null) return denied;

            var note = await db.Notes.FirstOrDefaultAsync(n => n.NoteId == noteId && n.EncounterId == id && !n.IsDeleted, ct);
            if (note is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            switch (SoapNoteRules.CanSign(note, me.Principal!.Subject))
            {
                case NoteOutcome.AlreadySigned:
                    return Problem(409, "already-signed", "The note is already signed.");
                case NoteOutcome.NotAuthor:
                    return Problem(403, "not-author", "Only the note's author may sign it.");
                case NoteOutcome.EmptyNote:
                    return Problem(422, "empty-note", "An empty note cannot be signed.");
            }

            note.IsSigned = true; note.SignedAt = clock.GetUtcNow();
            // The episode records THAT a note was signed and by whom — never what it says. This timeline is
            // read by reception and the call centre too (ADR-0031 §3).
            timeline.Add(CareSteps.NoteSigned, enc0.BeneficiaryId,
                encounterId: id, appointmentId: enc0.AppointmentId,
                actor: me.Principal!.Subject, reference: enc0.EncounterNo, occurredAt: note.SignedAt);
            await db.SaveChangesAsync(ct);
            await EmitAsync(audit, "emr_note", note.NoteId, AuditAction.StateChange, me, "{\"isSigned\":true}", ct);
            return Results.Ok(NoteResponse.From(note));
        }).RequireAuthorization(HbmpPolicies.Scope("emr:write"));

        // ---- Addendum to a (signed) note — the ONLY way to correct after signing ----
        enc.MapPost("/{id:guid}/notes/{noteId:guid}/addendum", async (
            Guid id, Guid noteId, CreateNoteRequest req, EmrDbContext db, ClinicalGate gate, IAuditClient audit,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var enc0 = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(e => e.EncounterId == id, ct);
            if (enc0 is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            var denied = await gate.CheckAsync("emr:write", EmrPolicies.Resources.Note, noteId.ToString(), enc0.BeneficiaryId, ct);
            if (denied is not null) return denied;

            var original = await db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.NoteId == noteId && n.EncounterId == id, ct);
            if (original is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var addendum = new EmrNote
            {
                NoteId = Guid.NewGuid(), EncounterId = id, NoteType = req.NoteType,
                Subjective = req.Subjective, Objective = req.Objective, Assessment = req.Assessment, Plan = req.Plan,
                AddendumOfNoteId = noteId, AuthoredBy = me.Principal!.Subject, AuthoredAt = clock.GetUtcNow(),
            };
            if (!SoapNoteRules.HasContent(addendum))
                return Problem(422, "empty-note", "An addendum must contain at least one populated section (S/O/A/P).");
            db.Notes.Add(addendum);
            await db.SaveChangesAsync(ct);
            await EmitAsync(audit, "emr_note", addendum.NoteId, AuditAction.Create, me, $"{{\"addendumOf\":\"{noteId}\"}}", ct);
            return Results.Created($"/api/v1/encounters/{id}/notes/{addendum.NoteId}", NoteResponse.From(addendum));
        }).RequireAuthorization(HbmpPolicies.Scope("emr:write"));

        // ---- Diagnosis (US-031): ICD-10 validated vs masterdata ----
        enc.MapPost("/{id:guid}/diagnoses", async (
            Guid id, AddDiagnosisRequest req, EmrDbContext db, ClinicalGate gate, IClinicalCodeValidator codes,
            IAuditClient audit, CareTimelineWriter timeline, IHbmpPrincipalAccessor me, HttpContext http,
            TimeProvider clock, CancellationToken ct) =>
        {
            var enc0 = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(e => e.EncounterId == id, ct);
            if (enc0 is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            var denied = await gate.CheckAsync("emr:write", EmrPolicies.Resources.Diagnosis, id.ToString(), enc0.BeneficiaryId, ct);
            if (denied is not null) return denied;

            if (string.IsNullOrWhiteSpace(req.IcdCode) ||
                !await codes.IcdExistsAsync(req.IcdCode, Bearer(http), ct))
                return Problem(422, "unknown-icd-code", $"ICD-10 code '{req.IcdCode}' is not present in master data.");

            var dx = new Diagnosis
            {
                DiagnosisId = Guid.NewGuid(), EncounterId = id, IcdCode = req.IcdCode,
                DiagnosisRank = req.DiagnosisRank, ClinicalStatus = req.ClinicalStatus,
                RecordedBy = me.Principal!.Subject, RecordedAt = clock.GetUtcNow(),
            };
            db.Diagnoses.Add(dx);
            // The step says a diagnosis was coded, not WHICH — the ICD code is the clinical content this
            // timeline deliberately does not carry. The reference is the encounter, which is the door to it
            // for anyone entitled to open it.
            timeline.Add(CareSteps.DiagnosisCoded, enc0.BeneficiaryId,
                encounterId: id, appointmentId: enc0.AppointmentId,
                actor: dx.RecordedBy, reference: enc0.EncounterNo, occurredAt: dx.RecordedAt);
            await db.SaveChangesAsync(ct);
            await EmitAsync(audit, "diagnosis", dx.DiagnosisId, AuditAction.Create, me, $"{{\"icd\":\"{dx.IcdCode}\"}}", ct);
            return Results.Created($"/api/v1/encounters/{id}/diagnoses/{dx.DiagnosisId}", DiagnosisResponse.From(dx));
        }).RequireAuthorization(HbmpPolicies.Scope("emr:write"));

        // ---- End the visit (23 §6) — the transition nothing in this platform performed ----
        //
        // `EncounterStatus.Completed` and `AppointmentStatus.Completed` have both existed since phase 1, and
        // `AppointmentWorkflow` has listed CheckedIn → Completed with the comment "encounter closed (phase 4)"
        // since phase 3. No code path ever wrote either, so every finished consultation stayed InProgress and
        // its appointment stayed CheckedIn — and the doctor's day list, which offers "Start visit" for any
        // CheckedIn appointment, kept offering it for patients who had already been seen and sent home.
        //
        // The appointment moves in the SAME transaction. Closing the visit and leaving the appointment open is
        // the state that caused this, and two endpoints for one clinical act is two chances to end up back in
        // it — the desk's board and the doctor's board would disagree about the same patient.
        enc.MapPost("/{id:guid}/complete", async (
            Guid id, EmrDbContext db, ClinicalGate gate, IAuditClient audit, IOutbox outbox,
            CareTimelineWriter timeline, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var encounter = await db.Encounters.FirstOrDefaultAsync(e => e.EncounterId == id, ct);
            if (encounter is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var denied = await gate.CheckAsync("emr:write", EmrPolicies.Resources.Encounter, id.ToString(), encounter.BeneficiaryId, ct);
            if (denied is not null) return denied;

            // Idempotent: closing an already-closed visit is the answer the caller wanted, not a conflict.
            // "Save & finalize" saves, signs and closes in sequence, so a retry after a partial failure must
            // not turn into an error the doctor cannot act on.
            if (encounter.Status == EncounterStatus.Completed)
                return Results.Ok(EncounterResponse.From(encounter));
            if (!EncounterWorkflow.CanComplete(encounter))
                return Problem(409, "encounter-not-open", $"the encounter is {encounter.Status}; only an in-progress visit can be closed.");
            if (!EncounterWorkflow.MayComplete(encounter, me.Principal?.Subject))
                return Problem(403, "not-the-treating-clinician", "only the clinician who opened this visit may close it.");

            var now = clock.GetUtcNow();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            encounter.Status = EncounterStatus.Completed;
            encounter.EndedAt = now;
            encounter.EndedBy = me.Principal?.Subject;

            // The queue entry is the patient sitting on a clinician's worklist. Leaving it Waiting after the
            // consultation ends is how a board shows a room that has already emptied.
            var queued = await db.QueueEntries.FirstOrDefaultAsync(q => q.EncounterId == id && q.State != QueueState.Done, ct);
            if (queued is not null) queued.State = QueueState.Done;

            Appointment? appt = null;
            if (encounter.AppointmentId is { } apptId)
            {
                appt = await db.Appointments.FirstOrDefaultAsync(a => a.AppointmentId == apptId, ct);
                // Only when the move is legal. A visit opened against an appointment that was later cancelled
                // still closes — the consultation happened — and the appointment keeps the state it reached.
                if (appt is not null && AppointmentWorkflow.CanTransition(appt.Status, AppointmentStatus.Completed))
                {
                    appt.Status = AppointmentStatus.Completed;
                    appt.UpdatedBy = me.Principal?.Subject;
                    appt.UpdatedAt = now;
                }
                else
                {
                    appt = null;
                }
            }

            timeline.Add(CareSteps.VisitEnded, encounter.BeneficiaryId,
                encounterId: encounter.EncounterId, appointmentId: encounter.AppointmentId,
                actor: encounter.EndedBy, reference: encounter.EncounterNo, occurredAt: now);
            await db.SaveChangesAsync(ct);
            await outbox.EnqueueAsync("EncounterCompleted", "emr.events", new
            {
                encounterId = encounter.EncounterId, encounter.EncounterNo,
                beneficiaryId = encounter.BeneficiaryId, appointmentId = encounter.AppointmentId,
                endedAt = now,
            }, ct);
            if (appt is not null)
                await outbox.EnqueueAsync("ApptCompleted", "emr.events", new
                {
                    appointmentId = appt.AppointmentId, beneficiaryId = appt.BeneficiaryId,
                    encounterId = encounter.EncounterId, locationId = appt.LocationId,
                }, ct);
            await tx.CommitAsync(ct);

            await EmitAsync(audit, "encounter", encounter.EncounterId, AuditAction.StateChange, me,
                $"{{\"status\":\"Completed\",\"appointmentClosed\":{(appt is not null).ToString().ToLowerInvariant()}}}", ct);
            return Results.Ok(EncounterResponse.From(encounter));
        }).RequireAuthorization(HbmpPolicies.Scope("emr:write"));

        // ---- Diagnosis retract (US-031) — a mis-keyed code, taken off the working assessment ----
        //
        // Not a hard delete: the row is flagged and stays, like every other clinical record here. And not
        // available after the encounter's note is signed — at that point the assessment is a signed clinical
        // statement, and the ONLY correction path is an addendum, exactly as it is for the note itself. A
        // retract endpoint that ignored that would be a back door around the sign-lock, undoing in one call
        // what SoapNoteRules refuses in another.
        enc.MapDelete("/{id:guid}/diagnoses/{diagnosisId:guid}", async (
            Guid id, Guid diagnosisId, EmrDbContext db, ClinicalGate gate, IAuditClient audit,
            IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var enc0 = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(e => e.EncounterId == id, ct);
            if (enc0 is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            var denied = await gate.CheckAsync("emr:write", EmrPolicies.Resources.Diagnosis, id.ToString(), enc0.BeneficiaryId, ct);
            if (denied is not null) return denied;

            var dx = await db.Diagnoses.FirstOrDefaultAsync(d => d.DiagnosisId == diagnosisId && d.EncounterId == id && !d.IsDeleted, ct);
            if (dx is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            if (await db.Notes.AsNoTracking().AnyAsync(n => n.EncounterId == id && n.IsSigned && !n.IsDeleted, ct))
                return Problem(409, "encounter-signed", "The encounter's note is signed — record the correction as an addendum.");
            if (!string.Equals(dx.RecordedBy, me.Principal!.Subject, StringComparison.Ordinal))
                return Problem(403, "not-recorder", "Only the clinician who recorded a diagnosis may retract it.");

            dx.IsDeleted = true;
            await db.SaveChangesAsync(ct);
            await EmitAsync(audit, "diagnosis", dx.DiagnosisId, AuditAction.SoftDelete, me, $"{{\"icd\":\"{dx.IcdCode}\",\"isDeleted\":true}}", ct);
            return Results.NoContent();
        }).RequireAuthorization(HbmpPolicies.Scope("emr:write"));

        // ---- Vital: per-type range + optional LOINC ----
        enc.MapPost("/{id:guid}/vitals", async (
            Guid id, AddVitalRequest req, EmrDbContext db, ClinicalGate gate, IClinicalCodeValidator codes,
            IAuditClient audit, CareTimelineWriter timeline, IHbmpPrincipalAccessor me, HttpContext http,
            TimeProvider clock, CancellationToken ct) =>
        {
            var enc0 = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(e => e.EncounterId == id, ct);
            if (enc0 is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            var denied = await gate.CheckAsync("emr:write", EmrPolicies.Resources.Vital, id.ToString(), enc0.BeneficiaryId, ct);
            if (denied is not null) return denied;

            if (VitalRange.Validate(req.VitalType, req.ValueNum) is { } err)
                return Problem(422, "vital-out-of-range", err);
            if (!await codes.LoincValidAsync(req.LoincCode, Bearer(http), ct))
                return Problem(422, "unknown-loinc-code", $"LOINC code '{req.LoincCode}' is not valid.");

            var vital = new Vital
            {
                VitalId = Guid.NewGuid(), EncounterId = id, VitalType = req.VitalType, ValueNum = req.ValueNum,
                Unit = req.Unit ?? VitalRange.CanonicalUnit(req.VitalType), LoincCode = req.LoincCode,
                RecordedBy = me.Principal!.Subject, MeasuredAt = req.MeasuredAt ?? clock.GetUtcNow(),
            };
            db.Vitals.Add(vital);
            timeline.Add(CareSteps.VitalsRecorded, enc0.BeneficiaryId,
                encounterId: id, appointmentId: enc0.AppointmentId,
                actor: vital.RecordedBy, reference: enc0.EncounterNo, occurredAt: vital.MeasuredAt);
            await db.SaveChangesAsync(ct);
            await EmitAsync(audit, "vital", vital.VitalId, AuditAction.Create, me, $"{{\"type\":\"{vital.VitalType}\"}}", ct);
            return Results.Created($"/api/v1/encounters/{id}/vitals/{vital.VitalId}", VitalResponse.From(vital));
        }).RequireAuthorization(HbmpPolicies.Scope("emr:write"));

        // ---- Beneficiary allergy list (beneficiary-level read) — treating clinician / oversight. pharmacy-service
        // calls this (token forwarded) to source allergies for advisory prescribe-time alerts (US-033). ----
        ben.MapGet("/{beneficiaryId:guid}/allergies", async (
            Guid beneficiaryId, EmrDbContext db, ClinicalGate gate, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync("emr:read", EmrPolicies.Resources.Allergy, beneficiaryId.ToString(), beneficiaryId, ct);
            if (denied is not null) return denied;
            var allergies = await db.Allergies.AsNoTracking()
                .Where(a => a.BeneficiaryId == beneficiaryId && !a.IsDeleted).ToListAsync(ct);
            return Results.Ok(allergies.Select(AllergyResponse.From));
        });

        // ---- Clinical-context oversight projection (16.6, H4) — the seam approvals /review calls to assemble the
        // reviewer's field-scoped context. Gated as an oversight read (medical_approval/director → emr:read-oversight,
        // Sensitive → engine-audited); min-necessary (a summary + signed-note assessments, no raw SOAP dump). Each
        // item carries SensitivityLevel + CallerHasAccess so the approvals projection enforces design 37 §6. emr's
        // own clinical records are Standard (the sensitive investigation RESULTS are orders-owned + gated there). ----
        ben.MapGet("/{beneficiaryId:guid}/clinical-context", async (
            Guid beneficiaryId, EmrDbContext db, ClinicalGate gate, FieldProjector projector,
            IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync("emr:read", EmrPolicies.Resources.Encounter, beneficiaryId.ToString(), beneficiaryId, ct);
            if (denied is not null) return denied;

            var encIds = await db.Encounters.AsNoTracking()
                .Where(e => e.BeneficiaryId == beneficiaryId).Select(e => e.EncounterId).ToListAsync(ct);
            var notes = await db.Notes.AsNoTracking()
                .Where(n => encIds.Contains(n.EncounterId) && n.IsSigned && !n.IsDeleted)
                .OrderByDescending(n => n.AuthoredAt).ToListAsync(ct);
            var activeDx = await db.Diagnoses.AsNoTracking()
                .Where(d => encIds.Contains(d.EncounterId) && d.ClinicalStatus == ClinicalStatus.Active && !d.IsDeleted)
                .ToListAsync(ct);

            await EmitAsync(audit, "clinical_context", beneficiaryId, AuditAction.Read, me,
                $"{{\"encounters\":{encIds.Count},\"notes\":{notes.Count},\"activeDx\":{activeDx.Count}}}", ct);

            // H2: each note's assessment is CLINICAL-class; route it through the FieldProjector so a caller without
            // the clinical field-class (e.g. an operational role) receives the note's existence but not its content.
            var p = me.Principal!;
            var projected = new List<IReadOnlyDictionary<string, object?>>();
            foreach (var n in notes)
                projected.Add(await projector.ProjectAsync(p, "clinical_note", new Dictionary<string, (object?, string)>(StringComparer.Ordinal)
                {
                    ["type"] = (n.NoteType.ToString(), "operational"),
                    ["author"] = (n.AuthoredBy, "operational"),
                    ["authoredAt"] = (n.AuthoredAt, "operational"),
                    ["summary"] = (n.Assessment ?? n.Plan ?? "(no assessment recorded)", DefaultPolicies.Classes.Clinical),
                    ["sensitivityLevel"] = ("Standard", "operational"),
                    ["callerHasAccess"] = (true, "operational"),
                }, ct));

            return Results.Ok(new
            {
                EmrSummary = $"{encIds.Count} encounter(s); {notes.Count} signed note(s); {activeDx.Count} active diagnosis(es).",
                Notes = projected,
                Documents = Array.Empty<object>(), // emr owns no documents; sensitive results are orders-owned + gated there
            });
        });

        // ---- Allergy (beneficiary-level): allergen validated vs masterdata ----
        ben.MapPost("/{beneficiaryId:guid}/allergies", async (
            Guid beneficiaryId, AddAllergyRequest req, EmrDbContext db, ClinicalGate gate, IClinicalCodeValidator codes,
            IAuditClient audit, IHbmpPrincipalAccessor me, HttpContext http, TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync("emr:write", EmrPolicies.Resources.Allergy, beneficiaryId.ToString(), beneficiaryId, ct);
            if (denied is not null) return denied;

            if (!await codes.AllergenExistsAsync(req.AllergenId, Bearer(http), ct))
                return Problem(422, "unknown-allergen", $"Allergen '{req.AllergenId}' is not present in master data.");

            var allergy = new Allergy
            {
                AllergyId = Guid.NewGuid(), BeneficiaryId = beneficiaryId, AllergenId = req.AllergenId,
                Reaction = req.Reaction, Severity = req.Severity, Status = req.Status,
                RecordedBy = me.Principal!.Subject, RecordedAt = clock.GetUtcNow(),
            };
            db.Allergies.Add(allergy);
            await db.SaveChangesAsync(ct);
            await EmitAsync(audit, "allergy", allergy.AllergyId, AuditAction.Create, me, $"{{\"severity\":\"{allergy.Severity}\"}}", ct);
            return Results.Created($"/api/v1/beneficiaries/{beneficiaryId}/allergies/{allergy.AllergyId}", AllergyResponse.From(allergy));
        }).RequireAuthorization(HbmpPolicies.Scope("emr:write"));

        // ---- Medication history (beneficiary-level): drug validated vs masterdata ----
        ben.MapPost("/{beneficiaryId:guid}/medication-history", async (
            Guid beneficiaryId, AddMedicationHistoryRequest req, EmrDbContext db, ClinicalGate gate, IClinicalCodeValidator codes,
            IAuditClient audit, IHbmpPrincipalAccessor me, HttpContext http, TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync("emr:write", EmrPolicies.Resources.MedicationHistory, beneficiaryId.ToString(), beneficiaryId, ct);
            if (denied is not null) return denied;

            if (!await codes.DrugExistsAsync(req.DrugId, Bearer(http), ct))
                return Problem(422, "unknown-drug", $"Drug '{req.DrugId}' is not present in master data.");

            var med = new MedicationHistory
            {
                MedHistoryId = Guid.NewGuid(), BeneficiaryId = beneficiaryId, DrugId = req.DrugId,
                Source = req.Source, StartDate = req.StartDate, EndDate = req.EndDate, Status = req.Status,
                RecordedBy = me.Principal!.Subject, RecordedAt = clock.GetUtcNow(),
            };
            db.MedicationHistories.Add(med);
            await db.SaveChangesAsync(ct);
            await EmitAsync(audit, "medication_history", med.MedHistoryId, AuditAction.Create, me, $"{{\"source\":\"{med.Source}\"}}", ct);
            return Results.Created($"/api/v1/beneficiaries/{beneficiaryId}/medication-history/{med.MedHistoryId}", MedicationHistoryResponse.From(med));
        }).RequireAuthorization(HbmpPolicies.Scope("emr:write"));

        // ---- FHIR R4 read projection over the canonical tables (interop, treating-gated) ----
        enc.MapGet("/{id:guid}/fhir", async (
            Guid id, EmrDbContext db, ClinicalGate gate, CancellationToken ct) =>
        {
            var enc0 = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(e => e.EncounterId == id, ct);
            if (enc0 is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            var denied = await gate.CheckAsync("emr:read", EmrPolicies.Resources.Encounter, id.ToString(), enc0.BeneficiaryId, ct);
            if (denied is not null) return denied;

            var dx = await db.Diagnoses.AsNoTracking().Where(d => d.EncounterId == id && !d.IsDeleted).ToListAsync(ct);
            var vitals = await db.Vitals.AsNoTracking().Where(v => v.EncounterId == id && !v.IsDeleted).ToListAsync(ct);
            var allergies = await db.Allergies.AsNoTracking().Where(a => a.BeneficiaryId == enc0.BeneficiaryId && !a.IsDeleted).ToListAsync(ct);
            var meds = await db.MedicationHistories.AsNoTracking().Where(m => m.BeneficiaryId == enc0.BeneficiaryId && !m.IsDeleted).ToListAsync(ct);

            var entries = new List<object> { FhirProjection.Encounter(enc0) };
            entries.AddRange(dx.Select(FhirProjection.Condition));
            entries.AddRange(vitals.Select(FhirProjection.Observation));
            entries.AddRange(allergies.Select(FhirProjection.AllergyIntolerance));
            entries.AddRange(meds.Select(FhirProjection.MedicationStatement));
            return Results.Ok(new { resourceType = "Bundle", type = "collection", entry = entries.Select(r => new { resource = r }) });
        });
    }

    private static string? Bearer(HttpContext http) => http.Request.Headers.Authorization.ToString();

    private static IResult Problem(int status, string type, string detail) =>
        Results.Problem(statusCode: status, title: type, detail: detail, type: $"urn:hbmp:{type}");

    private static ValueTask EmitAsync(IAuditClient audit, string entityType, Guid entityId, AuditAction action,
        IHbmpPrincipalAccessor me, string after, CancellationToken ct) =>
        audit.EmitAsync(new AuditEventDraft
        {
            EntityType = entityType, EntityId = entityId.ToString(), Action = action,
            ActorUserId = me.Principal?.Subject, AfterState = after,
        }, ct);
}
