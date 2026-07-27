using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Approvals.Api;

/// <summary>
/// Phase 19.4 — approvals-service's contribution to a utilization report: raised / approved / denied.
///
/// <para><b>Three counts, no clinical payload.</b> An authorization carries the requested service codes and,
/// through <c>/review</c>, clinical context — none of which crosses this boundary. Utilization is read by
/// Finance and the Network Team, and the ONLY thing they act on is the ratio: a group whose authorizations are
/// half denied has either a benefit-design problem or a provider-behaviour problem, and both are visible from
/// counts alone.</para>
/// </summary>
public static class UtilizationFactEndpoints
{
    public const int MaxBeneficiaries = 200;

    public static void MapUtilizationFacts(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/authorizations/utilization", async (
            string beneficiaryIds, DateOnly from, DateOnly to,
            ApprovalsDbContext db, IHbmpPrincipalAccessor me, IAuditClient audit, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return Results.Unauthorized();

            var ids = ParseIds(beneficiaryIds);
            if (ids.Count == 0) return Results.Ok(new { raised = 0, approved = 0, denied = 0 });
            if (ids.Count > MaxBeneficiaries)
                return Results.Problem(statusCode: 400, title: $"at most {MaxBeneficiaries} beneficiaryIds per request");

            var fromTs = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var toTs = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            // The window is on SUBMISSION, so an authorization raised inside it counts inside it even if it was
            // decided after. Windowing on the decision instead would make the raised total shrink whenever the
            // approvals queue is slow — turning a backlog into what looks like falling demand.
            var rows = await db.Authorizations.AsNoTracking()
                .Where(a => ids.Contains(a.BeneficiaryId) && a.SubmittedAt >= fromTs && a.SubmittedAt < toTs)
                .Select(a => a.Status)
                .ToListAsync(ct);

            // "Approved" includes the partial and emergency paths: from a utilization standpoint the member got
            // their care authorized. Denied is Rejected alone — InfoRequested is still open, and counting a
            // pending request as a denial would overstate refusals on exactly the queue that is slowest.
            var approved = rows.Count(s => s is AuthStatus.Approved or AuthStatus.PartiallyApproved
                or AuthStatus.EmergencyApproved or AuthStatus.Overridden or AuthStatus.Expired);
            var denied = rows.Count(s => s == AuthStatus.Rejected);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "authorization", EntityId = $"utilization:{ids.Count}-beneficiaries",
                Action = AuditAction.Read,
                ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId,
                DecisionOutcome = "counts-only",
                DecisionReasonCode = $"window:{from:yyyy-MM-dd}..{to:yyyy-MM-dd}",
            }, ct);

            return Results.Ok(new { raised = rows.Count, approved, denied });
        }).RequireAuthorization(HbmpPolicies.Scope("policy:read"));
    }

    /// <summary>Comma-separated ids; unparseable entries are dropped rather than failing the whole request.</summary>
    public static IReadOnlyList<Guid> ParseIds(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                     .Where(g => g is not null).Select(g => g!.Value).Distinct()];
}
