using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.CallCentre.Domain;
using Mersal.CallCentre.Infrastructure;

namespace Mersal.CallCentre.Api;

/// <summary>Phase 15.2 — member search + the minimum-necessary, CLINICAL-FREE Call Centre 360. A search hit carries
/// only a name and member number, enough to pick the right person. The 360 is 403 until the call has been bound to
/// that beneficiary by the agent's identity attestation (the <see cref="VerificationService"/> gate) — so a call
/// cannot read a member it was not opened against, and stops disclosing when it closes. Every search and every 360
/// read is an audited PHI read correlated by call_ref. Appointments span ALL branches (MemberScoped).</summary>
public static class Members
{
    public static void MapMembers(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/call-centre");

        // --- Pre-verification search (deliberately thin) ----------------------------------------------------
        v1.MapGet("/search", async (string? q, HttpRequest http, CallDeps deps, IMemberDirectory directory, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Read, "member-search", ct);
            if (denied is not null) return denied;
            if (string.IsNullOrWhiteSpace(q))
                // Name is listed because the index matches it and always has — omitting it here told agents the
                // one thing a caller always offers was not searchable.
                return Results.Problem(statusCode: 400, title: "q-required",
                    detail: "A query is required (name / phone / card or member no / national ID / passport / refugee ID / UNHCR no).");

            MemberSearchResult result;
            try
            {
                result = await directory.SearchAsync(q, http.Headers.Authorization, ct);
            }
            catch (SiblingRefusedException ex)
            {
                // A refusal upstream is a permissions/configuration fault, NOT an absent member. Reported as
                // itself so the agent does not tell a registered member they are not registered.
                await deps.AuditAsync("call_centre_search", q, AuditAction.Read, $"upstream refused ({ex.Status})",
                    callRef: null, fieldClasses: ["identity"]);
                return Results.Problem(statusCode: 502, title: "member-directory-unavailable",
                    type: "urn:hbmp:upstream-refused",
                    detail: "Member search could not be completed. This is a permissions or configuration fault, not an absent member.");
            }
            // Every search is audited (min-necessary: only the count + query class, no member content).
            await deps.AuditAsync("call_centre_search", q, AuditAction.Read, $"{result.MatchCount} match(es)",
                callRef: null, fieldClasses: ["identity"]);
            return Results.Ok(result);
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:read"));

        // --- Post-verification 360 (403 until verified for THIS interaction) --------------------------------
        v1.MapGet("/members/{beneficiaryId:guid}/summary", async (
            Guid beneficiaryId, Guid interactionId, HttpRequest http, CallDeps deps, IMemberDirectory directory, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(CallCentrePolicies.Read, "member-360", ct);
            if (denied is not null) return denied;

            // THE DISCLOSURE GATE. This call must be OPEN and BOUND to this beneficiary. A miss is 403 AND audited
            // — an attempt to read a member on a call that was never opened against them, or on a closed one.
            if (!await deps.Verification.IsVerifiedAsync(interactionId, beneficiaryId, ct))
            {
                var i = await deps.Db.Interactions.FindAsync([interactionId], ct);
                await deps.AuditAsync("call_centre_360", beneficiaryId.ToString(), AuditAction.Read,
                    "Denied360NotBound", i?.CallRef, severity: AuditSeverity.Warning);
                return Results.Problem(statusCode: 403, title: "not-verified",
                    detail: "This call is not open on that member. Open the member's file on an active call before reading their details.",
                    type: "urn:hbmp:callcentre-not-verified");
            }

            var summary = await directory.AssembleAsync(beneficiaryId, http.Headers.Authorization, interactionId, ct);
            if (summary is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var interaction = await deps.Db.Interactions.FindAsync([interactionId], ct);
            await deps.AuditAsync("call_centre_360", beneficiaryId.ToString(), AuditAction.Read, "Read360",
                interaction?.CallRef, severity: AuditSeverity.Notice,
                fieldClasses: ["identity", "coverage", "appointment", "contact"]);
            return Results.Ok(summary);
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:read"));
    }
}
