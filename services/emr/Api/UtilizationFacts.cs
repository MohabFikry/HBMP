using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Api;

/// <summary>
/// Phase 19.4 — the ONE fact emr-service contributes to a utilization report: how many encounters.
///
/// <para><b>A count, and nothing else.</b> Utilization is read by Finance, the Network Team and Beneficiary
/// Management — roles that must never receive clinical content (11-permission-matrix). The narrowness is
/// enforced HERE, at the owner of the data, rather than by the caller trimming a richer payload: a projection
/// applied after the wire is a projection that has already put PHI in a log, a trace and a retry buffer.</para>
///
/// <para>Deliberately NOT the encounter list with a <c>count()</c> on the client. Returning the list would mean
/// encounter ids, dates and provider ids crossing a service boundary to answer "how many", and every one of
/// those is a join away from a diagnosis.</para>
/// </summary>
public static class UtilizationFactEndpoints
{
    /// <summary>Matches the caller-side cap in policy-service. A longer query string is one a proxy may
    /// truncate, which would silently narrow the report to whoever fitted.</summary>
    public const int MaxBeneficiaries = 200;

    public static void MapUtilizationFacts(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/encounters/utilization", async (
            string beneficiaryIds, DateOnly from, DateOnly to,
            EmrDbContext db, IHbmpPrincipalAccessor me, IAuditClient audit, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return Results.Unauthorized();

            var ids = ParseIds(beneficiaryIds);
            if (ids.Count == 0)
                return Results.Ok(new { encounterCount = 0 });
            if (ids.Count > MaxBeneficiaries)
                return Results.Problem(statusCode: 400,
                    title: $"at most {MaxBeneficiaries} beneficiaryIds per request");

            // The window is on the encounter's own start, in UTC, because that is what emr stores. The caller
            // sends Cairo service dates; the inclusive upper bound below is the day AFTER `to`, so an encounter
            // at 22:00 on the last day of the window is inside it rather than lost to a timezone edge.
            var fromTs = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var toTs = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            var count = await db.Encounters.AsNoTracking()
                .Where(e => ids.Contains(e.BeneficiaryId) && e.StartedAt >= fromTs && e.StartedAt < toTs
                            && e.Status != EncounterStatus.Cancelled)
                .CountAsync(ct);

            // Counting someone's encounters is a read about their care even without a clinical value in the
            // response. Audited like any other PHI-adjacent read (19-audit-strategy).
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "encounter", EntityId = $"utilization:{ids.Count}-beneficiaries",
                Action = AuditAction.Read,
                ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId,
                DecisionOutcome = "count-only",
                DecisionReasonCode = $"window:{from:yyyy-MM-dd}..{to:yyyy-MM-dd}",
            }, ct);

            return Results.Ok(new { encounterCount = count });
        }).RequireAuthorization(HbmpPolicies.Scope("policy:read"));
    }

    /// <summary>Comma-separated ids; anything unparseable is DROPPED rather than failing the request, because
    /// one malformed id must not blank an entire group's report — and a dropped id can only ever make the
    /// count smaller, never invent care that did not happen.</summary>
    public static IReadOnlyList<Guid> ParseIds(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                     .Where(g => g is not null).Select(g => g!.Value).Distinct()];
}
