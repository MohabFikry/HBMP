using System.Text.Json;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Eligibility.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Eligibility.Api;

/// <summary>Coverage summary read for the case-service beneficiary-360 coordination view (Phase 10.1). It is the
/// fail-closed spine of that view, so it must exist for a coordinator to assemble a 360. Min-necessary: it returns
/// coverage STATUS + limit headroom for an assigned beneficiary — never clinical/EMR data (eligibility ≠ EMR).
/// Authorized by the caller's own <c>eligibility:check</c> scope (the case-service forwards the coordinator's token,
/// so this endpoint re-authorizes independently — defense in depth) and every read is audited as a PHI read.</summary>
public static class CoordinationEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapCoordination(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/beneficiaries").RequireAuthorization(HbmpPolicies.Scope("eligibility:check"));

        // GET /beneficiaries/{id}/coverage-summary — the coordination coverage card (status + limit headroom).
        g.MapGet("/{beneficiaryId:guid}/coverage-summary", async (
            Guid beneficiaryId, EligibilityDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me,
            CancellationToken ct) =>
        {
            var member = await db.Members.AsNoTracking()
                .FirstOrDefaultAsync(x => x.BeneficiaryId == beneficiaryId, ct);
            if (member is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            // Prefer an active coverage; fall back to the most recently updated one so the card is never empty
            // when only a lapsed coverage exists (the status field then communicates the lapse).
            var coverages = await db.Coverages.AsNoTracking()
                .Where(c => c.BeneficiaryId == beneficiaryId)
                .OrderByDescending(c => c.Status == "Active").ThenByDescending(c => c.UpdatedAt)
                .ToListAsync(ct);
            var coverage = coverages.FirstOrDefault();

            decimal? annual = null, remaining = null;
            if (coverage is not null)
            {
                foreach (var lim in ParseLimits(coverage.LimitsJson))
                {
                    // The annual money cap is the headroom a coordinator cares about; sum any monetary caps.
                    if (lim.LimitType.Equals("Annual", StringComparison.OrdinalIgnoreCase)
                        || lim.LimitType.Equals("Lifetime", StringComparison.OrdinalIgnoreCase))
                    {
                        annual = (annual ?? 0) + lim.LimitValue;
                        remaining = (remaining ?? 0) + Math.Max(0, lim.LimitValue - lim.ConsumedValue);
                    }
                }
            }

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "beneficiary_coverage_summary", EntityId = beneficiaryId.ToString(),
                Action = AuditAction.Read, ActorUserId = me.Principal?.Subject,
                DecisionOutcome = coverage?.Status ?? member.Status, FieldClasses = ["coverage", "eligibility"],
            }, ct);

            return Results.Ok(new CoverageSummaryResponse(
                DisplayName: $"{member.GivenName} {member.FamilyName}".Trim(),
                MemberId: member.MemberNo,
                Status: coverage?.Status ?? member.Status,
                PolicyName: coverage?.PolicyNo,
                CoverageCategory: coverage?.BenefitCategory,
                AnnualLimit: annual,
                RemainingLimit: remaining));
        })
        .Produces<CoverageSummaryResponse>();
    }

    private static IEnumerable<LimitRow> ParseLimits(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        List<LimitRow>? rows = null;
        try { rows = JsonSerializer.Deserialize<List<LimitRow>>(json, Json); }
        catch (JsonException) { yield break; }
        foreach (var r in rows ?? []) if (r is not null) yield return r;
    }

    private sealed record LimitRow(string LimitType, decimal LimitValue, decimal ConsumedValue);
}

/// <summary>Min-necessary coordination coverage card (no clinical fields). Shape mirrors the case-service assembler's
/// <c>CoverageDto</c> so the beneficiary-360 view binds directly.</summary>
public sealed record CoverageSummaryResponse(
    string DisplayName, string? MemberId, string Status, string? PolicyName,
    string? CoverageCategory, decimal? AnnualLimit, decimal? RemainingLimit);
