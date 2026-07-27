using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Api;

/// <summary>
/// Phase 19.4 — claims-service's contribution to a utilization report: what was claimed, allowed, and borne by
/// the member.
///
/// <para>This is the one utilization fact that is safe by construction rather than by projection: the claims
/// schema carries NO clinical column anywhere (36 §2) — codes and amounts only — so there is nothing clinical
/// here to withhold. What is still deliberate is the shape: three totals, not the claim list. A caller that
/// received lines could reconstruct a member's service history from CPT codes, which is a clinical picture
/// assembled out of financial parts.</para>
///
/// <para>Void claims are excluded. A voided claim did not happen; including it would inflate every period a
/// correction lands in, and corrections cluster in exactly the periods someone is investigating.</para>
/// </summary>
public static class UtilizationFactEndpoints
{
    public const int MaxBeneficiaries = 200;

    public static void MapUtilizationFacts(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/claims/utilization", async (
            string beneficiaryIds, DateOnly from, DateOnly to,
            ClaimsDbContext db, IHbmpPrincipalAccessor me, IAuditClient audit, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return Results.Unauthorized();

            var ids = ParseIds(beneficiaryIds);
            if (ids.Count == 0)
                return Results.Ok(new { claimed = 0m, approved = 0m, memberShare = 0m, currencyCode = "EGP" });
            if (ids.Count > MaxBeneficiaries)
                return Results.Problem(statusCode: 400, title: $"at most {MaxBeneficiaries} beneficiaryIds per request");

            // Windowed on the SERVICE date, not the submission date, so care delivered in March is March's
            // utilization however late the provider billed it. Billing lag is a claims-operations metric; it
            // must not move a member's consumption into the wrong period.
            var claims = await db.Claims.AsNoTracking().Include(c => c.Lines)
                .Where(c => ids.Contains(c.BeneficiaryId)
                            && c.ServiceDateFrom >= from && c.ServiceDateFrom <= to
                            && c.Status != ClaimStatus.Void)
                .Select(c => new
                {
                    c.ClaimedAmount,
                    c.ApprovedAmount,
                    c.CurrencyCode,
                    MemberShare = c.Lines.Where(l => l.Status != ClaimLineStatus.Void)
                                         .Sum(l => l.MemberShare ?? 0m),
                })
                .ToListAsync(ct);

            var currency = claims.Select(c => c.CurrencyCode).FirstOrDefault() ?? "EGP";

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "claim", EntityId = $"utilization:{ids.Count}-beneficiaries",
                Action = AuditAction.Read,
                ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId,
                DecisionOutcome = "totals-only",
                DecisionReasonCode = $"window:{from:yyyy-MM-dd}..{to:yyyy-MM-dd}",
                FieldClasses = ["financials"],
            }, ct);

            return Results.Ok(new
            {
                claimed = claims.Sum(c => c.ClaimedAmount),
                approved = claims.Sum(c => c.ApprovedAmount ?? 0m),
                memberShare = claims.Sum(c => c.MemberShare),
                currencyCode = currency,
            });
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
