using Mersal.Approvals.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Approvals.Api;

/// <summary>Phase 7.1 — the clinical review view (US-060). This is the ONLY endpoint that exposes clinical context
/// (EMR summary, clinical notes, supporting documents). It requires scope <c>auth:review</c> + role Medical Approval
/// (enforced by the shared engine over <see cref="ApprovalsPolicies"/>) under ABAC purpose <c>PUR</c>, returns a
/// field-scoped DTO assembled by <see cref="IClinicalContextProvider"/> (an explicit projection, never the raw
/// record), and writes a PHI-read audit event naming the fields returned. Finance/reception have no rule → 403.</summary>
public static class Review
{
    /// <summary>Purpose-of-use tag recorded on the PHI-read audit (19-audit-strategy; 11-permission-matrix §3.2).</summary>
    public const string Purpose = "PUR";

    public static void MapReview(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/authorizations/{id:guid}/review", async (
            Guid id, HttpRequest http,
            ApprovalsDbContext db, ApprovalsGate gate, IClinicalContextProvider clinical,
            IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            // Authorize the clinical read (tenant-scoped oversight). The engine audits this sensitive allow; we add
            // an explicit PHI-read record below naming the fields returned.
            var denied = await gate.CheckAsync(ApprovalsPolicies.Review, id.ToString(), Purpose, ct);
            if (denied is not null) return denied;

            var auth = await db.Authorizations.AsNoTracking().FirstOrDefaultAsync(a => a.AuthorizationId == id, ct);
            if (auth is null) return Results.NotFound();

            var bearer = http.Headers.Authorization.ToString();
            var ctx = await clinical.GetAsync(auth.BeneficiaryId, auth.SourceRef, bearer, ct);

            // PHI-read audit: actor, authorization id, purpose, and the exact field classes returned.
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "authorization", EntityId = auth.AuthorizationId.ToString(), Action = AuditAction.Read,
                ActorUserId = me.Principal?.Subject, ActorRole = string.Join(',', me.Principal?.Roles ?? new HashSet<string>()),
                TenantId = me.Principal?.TenantId, Purpose = Purpose,
                FieldClasses = ctx is null ? [] : ClinicalContext.FieldClasses,
                DecisionOutcome = "clinical-review-read", Severity = AuditSeverity.Notice,
            }, ct);

            return Results.Ok(ReviewView.From(auth, ctx));
        }).RequireAuthorization(HbmpPolicies.Scope("auth:review"));
    }
}
