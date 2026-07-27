using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Api;

/// <summary>
/// Phase 20 — the seam the patient profile's <c>pastMedicalHistory</c> and <c>encounters</c> sections read
/// (design 39 §2: "emr clinical context BECOMES the clinical section providers").
///
/// <para><b>Two sections, one call, one gate, one audit event.</b> profile-service asks once and derives both.
/// Splitting it in two would double the treating-relationship check, double the PHI-read audit event, and make
/// one clinician's single glance at a patient read as two accesses in an access review — which is exactly the
/// sort of noise that makes a review stop being read.</para>
///
/// <para>It reuses <see cref="ClinicalGate"/> unchanged, so the treating relationship (and the medical-approval
/// oversight route) binds here identically to every other clinical read. The profile adds section shaping on
/// top; it does not get its own, laxer door.</para>
/// </summary>
public static class ProfileContextEndpoint
{
    public static void MapProfileContext(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/beneficiaries/{beneficiaryId:guid}/profile-context", async (
            Guid beneficiaryId, EmrDbContext db, ITreatingRelationship treating, IAuditClient audit,
            IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return GateResults.Unauthenticated();

            // emr resolves the fact IT owns — the treating relationship — and then consults the shared
            // design-39 §4 matrix. It does NOT reuse EmrPolicies.Read here: that rule is doctor+treating, and
            // widening it so reception could read encounter LOGISTICS would widen every clinical read in the
            // service. The seam gets its own decision from the same table profile-service uses; both resolve
            // their own facts, so neither can stand in for the other (see ProfileSeam).
            var treats = await treating.TreatsAsync(principal.Subject, principal.ProviderId, beneficiaryId, ct);
            var context = ProfileSeam.ContextFor(principal, treatingRelationship: treats);
            var denied = ProfileSeam.Check(
                principal, context, ProfileSections.PastMedicalHistory, ProfileSections.Encounters);
            if (denied is not null) return denied;

            var encounters = await db.Encounters.AsNoTracking()
                .Where(e => e.BeneficiaryId == beneficiaryId)
                .OrderByDescending(e => e.StartedAt)
                .Take(200)
                .ToListAsync(ct);

            var encounterIds = encounters.ConvertAll(e => e.EncounterId);

            // ACTIVE conditions only. A resolved diagnosis from 2019 belongs in the record, but the profile's
            // past-medical-history section answers "what is true about this person now" — a list mixing the two
            // reads as a much sicker patient than the one in front of you.
            var diagnoses = await db.Diagnoses.AsNoTracking()
                .Where(d => encounterIds.Contains(d.EncounterId)
                            && d.ClinicalStatus == ClinicalStatus.Active && !d.IsDeleted)
                .OrderByDescending(d => d.RecordedAt)
                .ToListAsync(ct);

            // Appointments supply the LOGISTICS an encounter row shows — branch, type, and whether it began as
            // a referral or a follow-up. The clinical reason for the visit is not read from here: it is not an
            // appointment field, and inventing one would put free text into a section reception can see.
            var appointmentIds = encounters.Where(e => e.AppointmentId is not null)
                .Select(e => e.AppointmentId!.Value).Distinct().ToList();
            var appointments = await db.Appointments.AsNoTracking()
                .Where(a => appointmentIds.Contains(a.AppointmentId))
                .ToListAsync(ct);
            var appointmentById = appointments.ToDictionary(a => a.AppointmentId);

            // The narrative: the most recent SIGNED note's assessment. Unsigned notes are excluded — a draft a
            // clinician has not stood behind must not become another role's summary of the patient.
            var latestNote = await db.Notes.AsNoTracking()
                .Where(n => encounterIds.Contains(n.EncounterId) && n.IsSigned && !n.IsDeleted)
                .OrderByDescending(n => n.AuthoredAt)
                .FirstOrDefaultAsync(ct);

            // Display is the CODE. Resolving "E11" to "Type 2 diabetes mellitus" is masterdata-service's job
            // and its catalogue is bilingual — doing it here would put an English-only label into a payload the
            // Arabic UI renders, and would make emr a second place that answers "what does this code mean".
            var conditions = diagnoses.ConvertAll(d => new ProfileConditionView(
                "ICD-10", d.IcdCode, d.IcdCode, d.ClinicalStatus.ToString(),
                DateOnly.FromDateTime(d.RecordedAt.UtcDateTime)));

            var view = new ProfileContextView(
                conditions,
                latestNote?.Assessment ?? latestNote?.Plan,
                // emr owns no documents — the uploaded historical records on a member live in policy-service
                // (19.3b) and reach the profile through ITS provider. An empty array here rather than a second
                // document path: one place decides a document's classification.
                [],
                [.. encounters.Select(e =>
                {
                    var appointment = e.AppointmentId is { } id ? appointmentById.GetValueOrDefault(id) : null;
                    return new ProfileEncounterView(
                        e.EncounterNo,
                        e.StartedAt,
                        appointment?.BranchId?.ToString(),
                        e.CreatedBy,
                        appointment?.AppointmentType.ToString(),
                        // Deliberately null: see the appointment comment above.
                        null,
                        e.Status.ToString());
                })]);

            // ONE PHI-read audit event for the pair, naming the field classes served.
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "profile_context", EntityId = beneficiaryId.ToString(), Action = AuditAction.Read,
                ActorUserId = principal.Subject,
                ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId,
                Purpose = "patient-profile",
                DecisionOutcome = "ProfileContextRead",
                DecisionReasonCode = $"encounters:{encounters.Count};conditions:{conditions.Count}",
                FieldClasses = ["diagnosis", "clinical", "operational"],
                Severity = AuditSeverity.Notice,
            }, ct);

            return Results.Ok(view);
            // profile:read, not emr:read — reception and finance legitimately reach this seam for
            // encounter logistics and hold no clinical scope at all.
        }).RequireAuthorization(HbmpPolicies.Scope("profile:read"));
    }
}

/// <summary>A coded, currently-active condition for the profile's past-medical-history section.</summary>
public sealed record ProfileConditionView(
    string System, string Code, string Display, string? ClinicalStatus, DateOnly? OnsetOn);

/// <summary>A historical record uploaded against the member. Always empty from emr — policy-service owns
/// member documents (19.3b), and one place decides a document's classification.</summary>
public sealed record ProfileHistoricalRecordView(Guid LinkId, string DocumentClass, string Title, DateOnly? DocumentDate);

/// <summary>A visit as the profile shows it: logistics and status. <c>Reason</c> is always null from emr — the
/// clinical reason for a visit is not an appointment field, and the profile's meta variant strips it anyway.</summary>
public sealed record ProfileEncounterView(
    string EncounterRef, DateTimeOffset OccurredAt, string? BranchName,
    string? ClinicianName, string? Specialty, string? Reason, string Status);

/// <summary>The two sections, in one response.</summary>
public sealed record ProfileContextView(
    IReadOnlyList<ProfileConditionView> Conditions,
    string? Narrative,
    IReadOnlyList<ProfileHistoricalRecordView> UploadedRecords,
    IReadOnlyList<ProfileEncounterView> Encounters);
