using System.Globalization;
using System.Security.Claims;
using System.Text;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// 21.5 — the ACCESS REVIEW SNAPSHOT (design 40 §6).
///
/// This is the least-privilege review artifact: a point-in-time answer to "who can do what here, and why".
/// It is what a reviewer signs, so it has to be complete in the ways that matter — every membership with
/// its roles and status, every override WITH ITS REASON AND GRANTOR, and every platform admin. An override
/// listed without its reason is the failure mode this guards against: the reviewer sees an exception, has
/// no way to judge whether it is still justified, and either rubber-stamps it or escalates everything.
///
/// It lives in identity-service because that is where memberships, roles and overrides are. Branch grants
/// and programme enablement live in admin-service and are referenced by the report rather than copied
/// across a service boundary — 21.6 composes the two for the UI.
///
/// GENERATED SERVER-SIDE and AUDITED AS AN EXPORT: it is a bulk disclosure of who holds what, which is
/// exactly the kind of read that must leave a trace (19-audit-strategy).
/// </summary>
public static class AccessReviewEndpoints
{
    private const string Bearer = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;

    public static void MapAccessReview(this WebApplication app)
    {
        var g = app.MapGroup("/identity/admin/access-review").RequireAuthorization(IdentityAdminPolicies.Admin);

        g.MapGet("/{tenantId}", async (
            HttpContext http, string tenantId, IdentityStoreDbContext db, IEffectiveSetService effective,
            IAuditClient audit, TimeProvider clock, string? format) =>
        {
            var err = await Guard(http, "admin:read");
            if (err is not null) return err;

            var report = await BuildAsync(db, effective, tenantId, clock.GetUtcNow(), http.RequestAborted);

            // Audited as an EXPORT, not a read: this is a bulk disclosure of the tenant's whole access
            // posture, and the volume is the point. Recorded before the bytes leave, so a failure to write
            // the audit event cannot be followed by a successful download.
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "identity.access_review", EntityId = tenantId, Action = AuditAction.Export,
                ActorUserId = http.User.FindFirstValue(Claims.Subject),
                DecisionOutcome = "AccessReviewExported",
                AfterState = $"{{\"tenant\":\"{tenantId}\",\"memberships\":{report.Memberships.Count}," +
                             $"\"format\":\"{(format == "csv" ? "csv" : "json")}\"}}",
            });

            if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)) return Results.Ok(report);

            return Results.File(
                Encoding.UTF8.GetBytes(ToCsv(report)), "text/csv",
                $"access-review-{tenantId}-{report.GeneratedAt:yyyyMMdd}.csv");
        });
    }

    /// <summary>Build the snapshot. Public so the tests assert on the REPORT rather than on parsed CSV.</summary>
    public static async Task<AccessReviewReport> BuildAsync(
        IdentityStoreDbContext db, IEffectiveSetService effective, string tenantId,
        DateTimeOffset generatedAt, CancellationToken ct = default)
    {
        var memberships = await db.Memberships.AsNoTracking()
            .Where(m => m.TenantId == tenantId && !m.IsDeleted)
            .ToListAsync(ct);

        var rows = new List<AccessReviewMembership>();
        foreach (var m in memberships)
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == m.UserId, ct);

            var roles = await db.MembershipRoles.AsNoTracking()
                .Where(mr => mr.MembershipId == m.MembershipId)
                .Join(db.Roles, mr => mr.RoleId, r => r.Id, (_, r) => new { r.Name, r.Level })
                .ToListAsync(ct);

            var overrides = await db.Overrides.AsNoTracking()
                .Where(o => o.MembershipId == m.MembershipId && !o.IsDeleted)
                .ToListAsync(ct);

            var effectiveSet = await effective.ForMembershipAsync(m.MembershipId, ct);

            rows.Add(new AccessReviewMembership(
                m.MembershipId,
                user?.UserName ?? "(unknown)",
                user?.DisplayName ?? "",
                m.Status.ToString(),
                [.. roles.Select(r => r.Name!).OrderBy(n => n, StringComparer.Ordinal)],
                // The most privileged tier held, since level is "lower = more privileged".
                roles.Where(r => r.Level.HasValue).Select(r => r.Level!.Value).DefaultIfEmpty().Min(),
                user?.IsPlatformAdmin ?? false,
                [.. overrides.Select(o => new AccessReviewOverride(
                    o.ScopeKey, o.Effect.ToString(), o.Reason, o.GrantedBy, o.ValidUntil))],
                [.. (effectiveSet?.Keys ?? new HashSet<string>()).OrderBy(k => k, StringComparer.Ordinal)]));
        }

        // Per-key holder counts: the "who else has this" question a reviewer asks about every sensitive key,
        // and the one that is impossible to answer by reading the memberships one at a time.
        var holders = rows
            .SelectMany(r => r.EffectiveKeys.Select(k => (Key: k, r.MembershipId)))
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(gr => gr.Key, gr => gr.Count(), StringComparer.Ordinal);

        return new AccessReviewReport(
            tenantId, generatedAt,
            [.. rows.OrderBy(r => r.Username, StringComparer.Ordinal)],
            holders,
            rows.Count(r => r.IsPlatformAdmin));
    }

    /// <summary>CSV for the review pack. One row per membership; overrides are flattened WITH their reasons
    /// because a reviewer working in a spreadsheet must not have to open a second document to judge one.</summary>
    public static string ToCsv(AccessReviewReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine("username,display_name,status,roles,level,platform_admin,effective_keys,overrides");

        foreach (var m in report.Memberships)
        {
            var overrides = string.Join("; ", m.Overrides.Select(o =>
                $"{o.Effect} {o.ScopeKey} by {o.GrantedBy ?? "?"} until {o.ValidUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-"}: {o.Reason}"));

            sb.AppendLine(string.Join(',', new[]
            {
                Csv(m.Username), Csv(m.DisplayName), Csv(m.Status), Csv(string.Join(' ', m.Roles)),
                Csv(m.Level.ToString(CultureInfo.InvariantCulture)), Csv(m.IsPlatformAdmin ? "yes" : "no"),
                Csv(string.Join(' ', m.EffectiveKeys)), Csv(overrides),
            }));
        }
        return sb.ToString();
    }

    /// <summary>RFC-4180 quoting. Reasons are free text written by administrators — a comma or a quote in
    /// one would otherwise shift every later column and silently corrupt the evidence.</summary>
    private static string Csv(string? value)
    {
        var v = value ?? "";
        return v.Contains(',', StringComparison.Ordinal) || v.Contains('"', StringComparison.Ordinal)
            || v.Contains('\n', StringComparison.Ordinal)
            ? $"\"{v.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : v;
    }

    private static async Task<IResult?> Guard(HttpContext http, string scope)
    {
        var auth = await http.AuthenticateAsync(Bearer);
        if (!auth.Succeeded || auth.Principal is null)
            return Results.Problem(statusCode: 401, title: "unauthenticated");
        if (!auth.Principal.HasScope(scope))
            return Results.Problem(statusCode: 403, title: "insufficient-scope", detail: $"requires {scope}");
        if (!MfaEvaluator.IsSatisfied(auth.Principal.GetClaim(HbmpClaimTypes.Acr),
                                      auth.Principal.GetClaims(AccountPages.AmrClaim)))
            return Results.Problem(statusCode: 403, title: "mfa-required");
        return null;
    }
}

/// <summary>One override as the review shows it — always with its reason and grantor.</summary>
public sealed record AccessReviewOverride(
    string ScopeKey, string Effect, string Reason, string? GrantedBy, DateTimeOffset? ValidUntil);

/// <summary>One membership's access posture.</summary>
public sealed record AccessReviewMembership(
    Guid MembershipId, string Username, string DisplayName, string Status,
    IReadOnlyList<string> Roles, int Level, bool IsPlatformAdmin,
    IReadOnlyList<AccessReviewOverride> Overrides, IReadOnlyList<string> EffectiveKeys);

/// <summary>The point-in-time access review for one tenant.</summary>
public sealed record AccessReviewReport(
    string TenantId, DateTimeOffset GeneratedAt,
    IReadOnlyList<AccessReviewMembership> Memberships,
    IReadOnlyDictionary<string, int> HolderCountsByKey,
    int PlatformAdminCount);
