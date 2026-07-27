using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Profile.Domain;

namespace Mersal.Profile.Api;

/// <summary>
/// Phase 20.1 — the one canonical patient-profile endpoint (design 39).
///
/// <para>Three things happen here and nowhere else: the phase-15 call-centre verification gate is consumed
/// before any section is composed, the composition runs under the caller's own bearer, and exactly one
/// <c>ProfileViewed</c> audit event is written naming the sections actually served AND the ones withheld. That
/// last one is what makes "who looked at this patient, and what did they see" an answerable question — design 39
/// §7.5 calls it not optional, and it is the reason the audit is emitted from the endpoint rather than left to
/// each provider.</para>
/// </summary>
public static class ProfileEndpoints
{
    public static void MapProfile(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/patients");

        // ---- The profile ------------------------------------------------------------------------------------
        v1.MapGet("/{beneficiaryId:guid}/profile", async (
            Guid beneficiaryId, string? sections, string? purpose, Guid? interactionId,
            ProfileDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.AuthorizeAsync(
                ProfilePolicies.Read, beneficiaryId, purpose ?? "patient-profile", ct);
            if (denied is not null) return denied;

            var principal = deps.Principal!;
            var caller = deps.Caller();

            // THE CALL-CENTRE GATE, consumed not re-implemented (phase 15). An agent who has not verified the
            // caller gets 403 and an audit record of the attempt — the same answer the member 360 always gave.
            if (ProfilePolicies.RequiresCallCentreVerification(principal.Roles))
            {
                var verified = interactionId is { } id
                    && await deps.Verification.IsVerifiedAsync(id, beneficiaryId, caller, ct);
                if (!verified)
                {
                    await AuditAsync(deps, beneficiaryId, "Denied-NotVerified", "-", "-",
                        AuditSeverity.Warning, purpose, ct);
                    return Results.Problem(statusCode: 403, title: "not-verified",
                        detail: "The caller must be verified on this interaction before the profile can be opened.",
                        type: "urn:hbmp:callcentre-not-verified");
                }
            }

            var context = await deps.Facts.ResolveAsync(principal, beneficiaryId, caller, ct);
            var requested = ParseSections(sections);

            var result = await deps.Composer.ComposeAsync(beneficiaryId, context, requested, caller, ct);

            // ONE event per open, naming served AND withheld. A withheld section recorded as "not served" would
            // make an access review unable to distinguish "did not look" from "was not allowed to look".
            await AuditAsync(deps, beneficiaryId,
                CompositionReport.Describe(result.Profile.Sections),
                string.Join('|', result.Report.Served),
                string.Join('|', result.Report.Withheld),
                AuditSeverity.Notice, purpose, ct);

            return Results.Ok(result.Profile);
        }).RequireAuthorization(HbmpPolicies.Scope("profile:read"));

        // ---- The role-projected print/export summary --------------------------------------------------------
        // Generated SERVER-SIDE from the same projection — never from the rendered DOM, which would make the
        // export's contents a property of what the browser happened to have loaded (design 39 §6).
        v1.MapGet("/{beneficiaryId:guid}/profile/summary", async (
            Guid beneficiaryId, string? sections, string? purpose, ProfileDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.AuthorizeAsync(
                ProfilePolicies.Export, beneficiaryId, purpose ?? "profile-export", ct);
            if (denied is not null) return denied;

            var principal = deps.Principal!;
            var caller = deps.Caller();
            var context = await deps.Facts.ResolveAsync(principal, beneficiaryId, caller, ct);
            var result = await deps.Composer.ComposeAsync(beneficiaryId, context, ParseSections(sections), caller, ct);

            var summary = new ProfileExportSummary(
                result.Profile,
                // The watermark is on the payload, not decoration added by the client: an export that can be
                // printed without it is an export that leaves the building unattributed.
                new ExportWatermark(
                    deps.Subject ?? "(unknown)", deps.Roles ?? "(none)", result.Profile.ServedAt,
                    purpose ?? "profile-export"));

            // Audited as an EXPORT, separately from the read (design 39 §6). Copying a record out of the
            // platform is a different act from looking at it, and a review filters on that difference.
            await deps.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "patient_profile", EntityId = beneficiaryId.ToString(), Action = AuditAction.Export,
                ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant,
                Purpose = purpose ?? "profile-export",
                DecisionOutcome = "ProfileSummaryExported",
                DecisionReasonCode = string.Join('|', result.Report.Served),
                FieldClasses = [.. result.Report.Served],
                Severity = AuditSeverity.High,
            }, ct);

            return Results.Ok(summary);
        }).RequireAuthorization(HbmpPolicies.Scope("profile:export"));
    }

    /// <summary>
    /// Parse <c>?sections=header,alerts</c>. Unknown keys are DROPPED rather than rejected: the context bar and
    /// the full screen are shipped independently of the API, and a client asking for a section that does not
    /// exist yet should get the ones that do, not a 400.
    /// </summary>
    public static IReadOnlyCollection<string>? ParseSections(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var keys = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(ProfileSections.IsKnown)
            .ToList();
        // An entirely unrecognised list means "everything", not "nothing" — otherwise a typo silently returns an
        // empty profile, which reads exactly like a patient with no record.
        return keys.Count > 0 ? keys : null;
    }

    private static ValueTask AuditAsync(
        ProfileDeps deps, Guid beneficiaryId, string outcome, string served, string withheld,
        AuditSeverity severity, string? purpose, CancellationToken ct) =>
        deps.Audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "patient_profile", EntityId = beneficiaryId.ToString(), Action = AuditAction.Read,
            ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant,
            Purpose = purpose ?? "patient-profile",
            DecisionOutcome = "ProfileViewed",
            DecisionReasonCode = outcome,
            BeforeState = withheld.Length > 0 ? $"withheld:{withheld}" : null,
            AfterState = served.Length > 0 ? $"served:{served}" : null,
            Severity = severity,
        }, ct);
}

/// <summary>The printable summary: the same projection the screen received, plus provenance.</summary>
public sealed record ProfileExportSummary(PatientProfile Profile, ExportWatermark Watermark);

/// <summary>Who exported, in which role, when and why — stamped on the payload itself.</summary>
public sealed record ExportWatermark(string ViewerSubject, string ViewerRoles, DateTimeOffset GeneratedAt, string Purpose);
