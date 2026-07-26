using Mersal.Audit.Client;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.CallCentre.Infrastructure;

namespace Mersal.CallCentre.Api;

/// <summary>Phase 15.2 — member search + the minimum-necessary, CLINICAL-FREE Call Centre 360. Pre-verification the
/// agent sees only a name + which identifier TYPES to challenge on. The 360 is 403 until a verification PASS is
/// recorded for the interaction bound to that beneficiary (the <see cref="VerificationService"/> gate). Every search
/// and every 360 read is an audited PHI read correlated by call_ref. Appointments span ALL branches (MemberScoped).</summary>
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
                return Results.Problem(statusCode: 400, title: "q-required",
                    detail: "A query is required (phone / member no / national ID / passport / refugee ID / UNHCR no).");

            var result = await directory.SearchAsync(q, http.Headers.Authorization, ct);
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

            // THE VERIFICATION GATE. No member detail is disclosed until a Passed verification exists for this
            // interaction + beneficiary. A miss is 403 AND audited (an attempted disclosure without verification).
            if (!await deps.Verification.IsVerifiedAsync(interactionId, beneficiaryId, ct))
            {
                var i = await deps.Db.Interactions.FindAsync([interactionId], ct);
                await deps.AuditAsync("call_centre_360", beneficiaryId.ToString(), AuditAction.Read,
                    "Denied360NotVerified", i?.CallRef, severity: AuditSeverity.Warning);
                return Results.Problem(statusCode: 403, title: "not-verified",
                    detail: "The caller must be verified on this interaction before member details can be disclosed.",
                    type: "urn:hbmp:callcentre-not-verified");
            }

            var summary = await directory.AssembleAsync(beneficiaryId, http.Headers.Authorization, ct);
            if (summary is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var interaction = await deps.Db.Interactions.FindAsync([interactionId], ct);
            await deps.AuditAsync("call_centre_360", beneficiaryId.ToString(), AuditAction.Read, "Read360",
                interaction?.CallRef, severity: AuditSeverity.Notice,
                fieldClasses: ["identity", "coverage", "appointment", "contact"]);
            return Results.Ok(summary);
        }).RequireAuthorization(HbmpPolicies.Scope("callcentre:read"));
    }
}
