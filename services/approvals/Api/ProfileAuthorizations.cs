using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Microsoft.EntityFrameworkCore;
using Mersal.Time;

namespace Mersal.Approvals.Api;

/// <summary>
/// Phase 20 — the seam the patient profile's <c>authorizations</c> section reads.
///
/// <para>Every field the design-39 §4 matrix can narrow is projected here, so the profile's <c>status</c> and
/// <c>cost</c> variants have something to narrow FROM: the clinical <c>rationale</c> and the approved amount
/// are two different zones, and reception gets neither while finance gets only the second.</para>
/// </summary>
public static class ProfileAuthorizationsEndpoint
{
    public static void MapProfileAuthorizations(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/authorizations/for-beneficiary/{beneficiaryId:guid}", async (
            Guid beneficiaryId, ApprovalsDbContext db,
            IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return GateResults.Unauthenticated();

            // ApprovalsPolicies.List stays medical_approval/medical_director and still guards the reviewer
            // inbox. This seam consults the shared design-39 §4 matrix instead: reception genuinely needs the
            // authorization STATUS to answer "is my MRI approved", and it holds no approvals scope at all.
            // approvals-service owns no ABAC fact the matrix needs for this section, so the context is roles
            // only — and everything it does not own stays false, fail-closed.
            var denied = ProfileSeam.Check(
                principal, ProfileSeam.ContextFor(principal), ProfileSections.Authorizations);
            if (denied is not null) return denied;

            var authorizations = await db.Authorizations.AsNoTracking()
                .Where(a => a.BeneficiaryId == beneficiaryId)
                .OrderByDescending(a => a.SubmittedAt)
                .Take(200)
                .ToListAsync(ct);
            if (authorizations.Count == 0) return Results.Ok(new ProfileAuthorizationsView([]));

            var authIds = authorizations.ConvertAll(a => a.AuthorizationId);
            var decisions = await db.Decisions.AsNoTracking()
                .Where(d => authIds.Contains(d.AuthorizationId))
                .ToListAsync(ct);

            var rows = authorizations.ConvertAll(a =>
            {
                var decision = decisions
                    .Where(d => d.AuthorizationId == a.AuthorizationId)
                    .OrderByDescending(d => d.DecidedAt)
                    .FirstOrDefault();

                return new ProfileAuthorizationView(
                    a.AuthNo,
                    a.Source.ToString(),
                    a.Status.ToString(),
                    a.SubmittedAt,
                    a.DecidedAt,
                    // The validity window an authorization is honoured within. Reception reads this to tell a
                    // member "your MRI is approved until the 30th", which is the section's whole purpose there.
                    a.DecidedAt is { } decided ? BusinessCalendar.DateIn(decided).AddDays(30) : null,
                    decision?.Rationale,
                    null);
            });

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "profile_authorizations", EntityId = beneficiaryId.ToString(),
                Action = AuditAction.Read,
                ActorUserId = principal.Subject,
                ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId,
                Purpose = "patient-profile",
                DecisionOutcome = "ProfileAuthorizationsRead",
                DecisionReasonCode = $"authorizations:{rows.Count}",
                FieldClasses = ["approval_status"],
                Severity = AuditSeverity.Notice,
            }, ct);

            return Results.Ok(new ProfileAuthorizationsView(rows));
        }).RequireAuthorization(HbmpPolicies.Scope("profile:read"))
        .Produces<ProfileAuthorizationsView>();
    }
}

/// <summary>An authorization as the profile shows it. <c>ApprovedAmount</c> is null: approvals-service decides
/// medical necessity, not price — the money lives in claims, and the profile's financial section reads it from
/// there. A nullable field rather than none, because the design-39 §4 <c>cost</c> variant names it and the
/// pricing seam belongs on this shape when it exists.</summary>
public sealed record ProfileAuthorizationView(
    string AuthNo, string? ServiceCategory, string Status, DateTimeOffset RequestedAt,
    DateTimeOffset? DecidedAt, DateOnly? ValidUntil, string? Rationale, decimal? ApprovedAmount);

public sealed record ProfileAuthorizationsView(IReadOnlyList<ProfileAuthorizationView> Items);
