using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Api;

/// <summary>
/// Phase 20 — the seams the patient profile's <c>prescriptions</c> and <c>referrals</c> sections read.
///
/// <para>Both consult the shared design-39 §4 matrix through <see cref="ProfileSeam"/>, on the treating fact
/// pharmacy resolves for itself. <see cref="PharmacyPolicies.RxRead"/> is untouched and still guards the
/// dispensing surface at its original width.</para>
///
/// <para><c>?scope=own</c> narrows the prescription list to the CALLING pharmacy under provider-ownership,
/// applied here rather than in the aggregator — a dispensing pharmacy sees the prescriptions it is filling,
/// not the member's whole medication record.</para>
/// </summary>
public static class ProfileSectionEndpoints
{
    public static void MapProfileSections(this IEndpointRouteBuilder app)
    {
        // ---- Prescriptions & dispensing --------------------------------------------------------------------
        app.MapGet("/api/v1/prescriptions/for-beneficiary/{beneficiaryId:guid}", async (
            Guid beneficiaryId, string? scope, HttpRequest http, PharmacyDbContext db,
            ITreatingRelationshipClient treating,
            IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return GateResults.Unauthenticated();

            // PharmacyPolicies.RxRead stays doctor+treating and still guards the dispensing surface. This seam
            // gets its own decision from the shared matrix, on the fact pharmacy can resolve.
            var treats = await treating.TreatsAsync(
                beneficiaryId, http.Headers.Authorization.FirstOrDefault(), ct);
            var denied = ProfileSeam.Check(
                principal, ProfileSeam.ContextFor(principal, treatingRelationship: treats),
                ProfileSections.Prescriptions);
            if (denied is not null) return denied;

            var prescriptions = await db.Prescriptions.AsNoTracking().Include(p => p.Lines)
                .Where(p => p.BeneficiaryId == beneficiaryId)
                .OrderByDescending(p => p.SubmittedAt)
                .Take(200)
                .ToListAsync(ct);
            if (prescriptions.Count == 0) return Results.Ok(new ProfilePrescriptionsView([]));

            var lineIds = prescriptions.SelectMany(p => p.Lines).Select(l => l.PrescriptionLineId).ToList();
            var dispenses = await db.DispenseEvents.AsNoTracking()
                .Where(d => lineIds.Contains(d.PrescriptionLineId))
                .ToListAsync(ct);

            // PROVIDER OWNERSHIP. A pharmacy's profile asks for `own`: the lines IT dispensed. Filtering here
            // means the rule lives with the data — an aggregator that filtered instead would be an aggregator
            // that could stop filtering.
            var ownOnly = string.Equals(scope, "own", StringComparison.OrdinalIgnoreCase);
            var callerPharmacy = Guid.TryParse(principal.ProviderId, out var pg) ? pg : Guid.Empty;
            var ownLineIds = dispenses.Where(d => d.DispensingPharmacyId == callerPharmacy)
                .Select(d => d.PrescriptionLineId).ToHashSet();

            var rows = new List<ProfilePrescriptionView>();
            foreach (var rx in prescriptions)
            {
                foreach (var line in rx.Lines)
                {
                    if (ownOnly && !ownLineIds.Contains(line.PrescriptionLineId)) continue;

                    var dispense = dispenses
                        .Where(d => d.PrescriptionLineId == line.PrescriptionLineId)
                        .OrderByDescending(d => d.DispensedAt)
                        .FirstOrDefault();

                    rows.Add(new ProfilePrescriptionView(
                        rx.RxNo,
                        // The drug id, not a resolved trade name: masterdata owns the bilingual drug catalogue,
                        // and resolving it here would make pharmacy a second answer to "what is this drug".
                        line.DrugId.ToString(),
                        line.Status.ToString(),
                        rx.SubmittedAt ?? default,
                        dispense?.DispensedAt,
                        dispense?.BatchNo,
                        dispense?.ExpiryDate,
                        dispense?.SubstitutedDrugId?.ToString()));
                }
            }

            await AuditAsync(audit, me, "prescriptions", beneficiaryId, rows.Count, ownOnly, ct);
            return Results.Ok(new ProfilePrescriptionsView(rows));
        }).RequireAuthorization(HbmpPolicies.Scope("profile:read"));

        // ---- Referrals ------------------------------------------------------------------------------------
        app.MapGet("/api/v1/referrals/for-beneficiary/{beneficiaryId:guid}", async (
            Guid beneficiaryId, HttpRequest http, PharmacyDbContext db,
            ITreatingRelationshipClient treating,
            IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return GateResults.Unauthenticated();

            var treats = await treating.TreatsAsync(
                beneficiaryId, http.Headers.Authorization.FirstOrDefault(), ct);
            var denied = ProfileSeam.Check(
                principal, ProfileSeam.ContextFor(principal, treatingRelationship: treats),
                ProfileSections.Referrals);
            if (denied is not null) return denied;

            var referrals = await db.Referrals.AsNoTracking()
                .Where(r => r.BeneficiaryId == beneficiaryId)
                .OrderByDescending(r => r.RequestedAt)
                .Take(200)
                .ToListAsync(ct);

            // The referral REASON is deliberately not projected. It is free clinical text ("query malignancy"),
            // and the profile's referrals section is visible to reception and beneficiary management — who need
            // to know a referral exists, to which specialty, and whether the loop closed.
            var rows = referrals.ConvertAll(r => new ProfileReferralView(
                r.ReferralNo,
                r.Status.ToString(),
                r.TargetSpecialty,
                r.RequestedAt,
                r.Status is ReferralStatus.Completed or ReferralStatus.Cancelled ? r.RequestedAt : null));

            await AuditAsync(audit, me, "referrals", beneficiaryId, rows.Count, ownOnly: false, ct);
            return Results.Ok(new ProfileReferralsView(rows));
        }).RequireAuthorization(HbmpPolicies.Scope("profile:read"));
    }

    private static ValueTask AuditAsync(
        IAuditClient audit, IHbmpPrincipalAccessor me, string section, Guid beneficiaryId,
        int count, bool ownOnly, CancellationToken ct) =>
        audit.EmitAsync(new AuditEventDraft
        {
            EntityType = $"profile_{section}", EntityId = beneficiaryId.ToString(), Action = AuditAction.Read,
            ActorUserId = me.Principal?.Subject,
            ActorRole = me.Principal is null ? null : string.Join(',', me.Principal.Roles),
            TenantId = me.Principal?.TenantId,
            Purpose = "patient-profile",
            DecisionOutcome = "ProfileSectionRead",
            DecisionReasonCode = $"{section}:{count};scope:{(ownOnly ? "own" : "all")}",
            FieldClasses = [section == "referrals" ? "referral" : "prescription"],
            Severity = AuditSeverity.Notice,
        }, ct);
}

public sealed record ProfilePrescriptionView(
    string RxNo, string DrugDisplay, string Status, DateTimeOffset PrescribedOn,
    DateTimeOffset? DispensedOn, string? BatchNo, DateOnly? ExpiryDate, string? SubstitutedWith);

public sealed record ProfilePrescriptionsView(IReadOnlyList<ProfilePrescriptionView> Items);

/// <summary>A referral as the profile shows it — existence, specialty and loop status. <b>No reason:</b> that
/// is free clinical text, and this section reaches reception and beneficiary management.</summary>
public sealed record ProfileReferralView(
    string ReferralNo, string Status, string? TargetSpecialty,
    DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt);

public sealed record ProfileReferralsView(IReadOnlyList<ProfileReferralView> Items);
