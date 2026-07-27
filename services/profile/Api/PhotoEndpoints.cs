using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Profile.Infrastructure;

namespace Mersal.Profile.Api;

/// <summary>
/// Phase 20.3 — the beneficiary photo. Identity-sensitive, biometric-adjacent data for a refugee population
/// (design 39 §5), so it is treated as a separate resource with a separate, narrower allow-list rather than as
/// one more field on the header.
///
/// <para><b>This endpoint never serves bytes.</b> It resolves a SHORT-TTL SIGNED url from policy-service — which
/// owns the member document, its classification and its consent linkage — audits the retrieval, and redirects.
/// A permanent or guessable URL would survive the session, the role and the consent that justified it; a
/// redirect to a signature that expires in minutes does not.</para>
/// </summary>
public static class PhotoEndpoints
{
    public static void MapPhoto(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/patients/{beneficiaryId:guid}/photo", async (
            Guid beneficiaryId, ProfileDeps deps, CallerScopedHttp http, CancellationToken ct) =>
        {
            // The photo allow-list is NARROWER than the profile's: reception, the call centre, clinicians,
            // beneficiary management. Finance, claims, labs, pharmacies and platform admins are denied here even
            // though several of them may open the profile itself.
            var denied = await deps.AuthorizeAsync(ProfilePolicies.Photo, beneficiaryId, "identification", ct);
            if (denied is not null) return denied;

            var caller = deps.Caller();
            using var doc = await http.GetAsync(
                "policy", $"/api/v1/beneficiaries/{beneficiaryId}/identity-photo", caller, ct);

            // No photo, or consent was refused — both are ordinary, and neither blocks care. The SPA renders an
            // initials avatar. 404 rather than a placeholder image, so "no photo" is a fact the client can act on.
            if (doc is null) return Results.NotFound();

            var url = doc.RootElement.Str("signedUrl");
            if (string.IsNullOrWhiteSpace(url)) return Results.NotFound();

            // Every retrieval is audited (design 39 §5). A photo read is a disclosure of a person's face to a
            // named user at a named time, and that is exactly the sort of access a data-subject request asks about.
            await deps.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "identity_photo", EntityId = beneficiaryId.ToString(), Action = AuditAction.Read,
                ActorUserId = deps.Subject, ActorRole = deps.Roles, TenantId = deps.Tenant,
                Purpose = "identification",
                DecisionOutcome = "IdentityPhotoViewed",
                DecisionReasonCode = doc.RootElement.Str("linkId"),
                FieldClasses = ["identity", "biometric-adjacent"],
                Severity = AuditSeverity.Notice,
            }, ct);

            // 302 to the short-TTL signature. The bytes never pass through this service, so there is no copy of
            // a refugee's photograph in the composition layer's memory, logs or traces.
            return Results.Redirect(url, permanent: false, preserveMethod: false);
        }).RequireAuthorization(HbmpPolicies.Scope("profile:read"));
    }
}
