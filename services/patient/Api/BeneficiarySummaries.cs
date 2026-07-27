using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Patient.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Patient.Api;

/// <summary>
/// Phase 19.5 — names for a page of somebody else's list.
///
/// <para>policy-service's member query returns memberships. A membership list with no names is unusable at a
/// counter, and the names live here. The alternatives were both worse: 25 round trips per page, or a copy of
/// the name in the policy schema that goes stale the first time somebody corrects a spelling.</para>
///
/// <para>NARROW AT THE OWNER, exactly as the 19.4 fact endpoints are. This returns name + status and NOTHING
/// else — no identifiers, no contacts, no date of birth. A list is the highest-volume disclosure the platform
/// makes, and the right place to decide what a list may contain is the service that knows what the fields
/// mean, before they are on the wire, in a trace and in a retry buffer.</para>
/// </summary>
public static class BeneficiarySummaryEndpoints
{
    /// <summary>One page's worth. A caller wanting more is running an extract, which has its own gated,
    /// filter-snapshotted path (19.5b) — not this one.</summary>
    public const int MaxIds = 100;

    public static void MapBeneficiarySummaries(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/beneficiaries/summaries", async (
            string ids, PatientDbContext db, IHbmpPrincipalAccessor me, IAuditClient audit, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return Results.Unauthorized();

            var wanted = ParseIds(ids);
            if (wanted.Count == 0) return Results.Ok(Array.Empty<object>());
            if (wanted.Count > MaxIds)
                return Results.Problem(statusCode: 400, title: $"at most {MaxIds} ids per request");

            var rows = await db.Beneficiaries.AsNoTracking()
                .Where(b => wanted.Contains(b.BeneficiaryId) && !b.IsDeleted)
                .Select(b => new
                {
                    b.BeneficiaryId,
                    b.GivenName,
                    b.FamilyName,
                    Status = b.Status.ToString(),
                })
                .ToListAsync(ct);

            // A name is PII and this is a disclosure of many at once, so it is audited as a search — with the
            // count, which is the number a later review needs and cannot reconstruct.
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "beneficiary", EntityId = $"summaries:{rows.Count}",
                Action = AuditAction.Read,
                ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId,
                DecisionOutcome = "name-only",
                DecisionReasonCode = "member-query-page",
                FieldClasses = ["identity"],
            }, ct);

            return Results.Ok(rows);
        }).RequireAuthorization(HbmpPolicies.Scope("patient:read"));
    }

    /// <summary>Comma-separated ids; unparseable entries are dropped rather than failing the whole page.</summary>
    public static IReadOnlyList<Guid> ParseIds(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                     .Where(g => g is not null).Select(g => g!.Value).Distinct()];
}
