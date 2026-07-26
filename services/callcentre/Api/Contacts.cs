using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.CallCentre.Domain;
using Mersal.CallCentre.Infrastructure;

namespace Mersal.CallCentre.Api;

/// <summary>Phase 15.4 — contact corrections from the call. Post-verification only (403 + audit otherwise); the
/// value is validated server-side (invalid → 422 before anything is persisted); the change is forwarded to
/// patient-service (which owns the one-primary rule + history — corrections are updates with history, never silent
/// overwrites) and audited with the call_ref. Referrals/follow-ups are surfaced in the 360 (15.2) and convert to a
/// booking via 15.3 (referralRef → appointmentType=Referral), so no extra endpoint is needed here.</summary>
public static class Contacts
{
    public static void MapContacts(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/call-centre/members");

        // --- Correct an existing contact --------------------------------------------------------------------
        v1.MapPatch("/{beneficiaryId:guid}/contacts/{contactId:guid}", async (
            Guid beneficiaryId, Guid contactId, UpdateContactFromCallRequest req, HttpRequest http,
            CallDeps deps, IContactGateway patient, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Act, "update-contact", ct);
            if (denied is not null) return denied;
            var gate = await RequireVerified(deps, req.InteractionId, beneficiaryId, ct);
            if (gate is not null) return gate;
            if (!ContactValidation.IsValid(req.Kind, req.Value))
                return Results.Problem(statusCode: 422, title: "invalid-contact",
                    detail: $"'{req.Value}' is not a valid {req.Kind}.", type: "urn:hbmp:invalid-contact");

            var result = await patient.UpdateContactAsync(beneficiaryId, contactId,
                new { value = req.Value, kind = req.Kind, preferredChannel = req.PreferredChannel }, http.Headers.Authorization, ct);
            if (result.IsSuccess)
                await AuditContact(deps, req.InteractionId, beneficiaryId, contactId.ToString(), "ContactUpdated", req.Kind, ct);
            return Passthrough(result);
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:act"));

        // --- Add a new contact ------------------------------------------------------------------------------
        v1.MapPost("/{beneficiaryId:guid}/contacts", async (
            Guid beneficiaryId, AddContactFromCallRequest req, HttpRequest http,
            CallDeps deps, IContactGateway patient, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Act, "add-contact", ct);
            if (denied is not null) return denied;
            var gate = await RequireVerified(deps, req.InteractionId, beneficiaryId, ct);
            if (gate is not null) return gate;
            if (!ContactValidation.IsValid(req.Kind, req.Value))
                return Results.Problem(statusCode: 422, title: "invalid-contact",
                    detail: $"'{req.Value}' is not a valid {req.Kind}.", type: "urn:hbmp:invalid-contact");

            var result = await patient.AddContactAsync(beneficiaryId,
                new { kind = req.Kind, value = req.Value, isPrimary = req.IsPrimary, preferredChannel = req.PreferredChannel },
                http.Headers.Authorization, ct);
            if (result.IsSuccess)
                await AuditContact(deps, req.InteractionId, beneficiaryId, "new", "ContactAdded", req.Kind, ct);
            return Passthrough(result);
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:act"));
    }

    private static async Task<IResult?> RequireVerified(CallDeps deps, Guid interactionId, Guid beneficiaryId, CancellationToken ct)
    {
        if (await deps.Verification.IsVerifiedAsync(interactionId, beneficiaryId, ct)) return null;
        var i = await deps.Db.Interactions.FindAsync([interactionId], ct);
        await deps.AuditAsync("call_centre_contact", beneficiaryId.ToString(), AuditAction.Update,
            "DeniedContactNotVerified", i?.CallRef, severity: AuditSeverity.Warning);
        return Results.Problem(statusCode: 403, title: "not-verified",
            detail: "The caller must be verified on this interaction before editing contacts.",
            type: "urn:hbmp:callcentre-not-verified");
    }

    private static async Task AuditContact(CallDeps deps, Guid interactionId, Guid beneficiaryId, string contactId, string outcome, string kind, CancellationToken ct)
    {
        var i = await deps.Db.Interactions.FindAsync([interactionId], ct);
        // BEFORE/AFTER minimized to the KIND changed — never the old/new value in the audit trail.
        await deps.AuditAsync("call_centre_contact", $"{beneficiaryId}:{contactId}", AuditAction.Update, outcome,
            i?.CallRef, severity: AuditSeverity.Notice, after: kind, fieldClasses: ["contact"]);
    }

    private static IResult Passthrough(GatewayResult r) =>
        Results.Content(r.Body ?? "", "application/json", statusCode: r.StatusCode);
}
