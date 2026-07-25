using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
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

        // ---- Full clinical record (US-030) — treating clinician or approval team only ----
        enc.MapGet("/{id:guid}/clinical", async (
            Guid id, EmrDbContext db, ClinicalGate gate, HttpContext http, CancellationToken ct) =>
        {
            var enc0 = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(e => e.EncounterId == id, ct);
            if (enc0 is null) return Results.NotFound();

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
            if (enc0 is null) return Results.NotFound();
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
            if (enc0 is null) return Results.NotFound();
            var denied = await gate.CheckAsync("emr:write", EmrPolicies.Resources.Note, noteId.ToString(), enc0.BeneficiaryId, ct);
            if (denied is not null) return denied;

            var note = await db.Notes.FirstOrDefaultAsync(n => n.NoteId == noteId && n.EncounterId == id && !n.IsDeleted, ct);
            if (note is null) return Results.NotFound();

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
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var enc0 = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(e => e.EncounterId == id, ct);
            if (enc0 is null) return Results.NotFound();
            var denied = await gate.CheckAsync("emr:write", EmrPolicies.Resources.Note, noteId.ToString(), enc0.BeneficiaryId, ct);
            if (denied is not null) return denied;

            var note = await db.Notes.FirstOrDefaultAsync(n => n.NoteId == noteId && n.EncounterId == id && !n.IsDeleted, ct);
            if (note is null) return Results.NotFound();

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
            if (enc0 is null) return Results.NotFound();
            var denied = await gate.CheckAsync("emr:write", EmrPolicies.Resources.Note, noteId.ToString(), enc0.BeneficiaryId, ct);
            if (denied is not null) return denied;

            var original = await db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.NoteId == noteId && n.EncounterId == id, ct);
            if (original is null) return Results.NotFound();

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
            IAuditClient audit, IHbmpPrincipalAccessor me, HttpContext http, TimeProvider clock, CancellationToken ct) =>
        {
            var enc0 = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(e => e.EncounterId == id, ct);
            if (enc0 is null) return Results.NotFound();
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
            await db.SaveChangesAsync(ct);
            await EmitAsync(audit, "diagnosis", dx.DiagnosisId, AuditAction.Create, me, $"{{\"icd\":\"{dx.IcdCode}\"}}", ct);
            return Results.Created($"/api/v1/encounters/{id}/diagnoses/{dx.DiagnosisId}", DiagnosisResponse.From(dx));
        }).RequireAuthorization(HbmpPolicies.Scope("emr:write"));

        // ---- Vital: per-type range + optional LOINC ----
        enc.MapPost("/{id:guid}/vitals", async (
            Guid id, AddVitalRequest req, EmrDbContext db, ClinicalGate gate, IClinicalCodeValidator codes,
            IAuditClient audit, IHbmpPrincipalAccessor me, HttpContext http, TimeProvider clock, CancellationToken ct) =>
        {
            var enc0 = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(e => e.EncounterId == id, ct);
            if (enc0 is null) return Results.NotFound();
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
            if (enc0 is null) return Results.NotFound();
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
