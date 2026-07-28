using System.Security.Claims;
using Mersal.Audit.Client;
using Mersal.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// 21.5 — session and sign-in-history surfaces (design 40 §6, 18 §9).
///
/// TWO AUDIENCES, and the split is the design. A person may see and end THEIR OWN sessions without any
/// administrative scope — being able to sign out a device you no longer trust is a safety feature, and
/// putting it behind an admin scope means the people most likely to need it (a clinician whose phone was
/// stolen) cannot use it. An administrator may see and end ANYONE's, and that requires admin:write plus an
/// MFA session, because ending someone else's session is an intervention in their work.
///
/// Neither surface returns credential material of any kind — see NoCredentialMaterialInAuditTests.
/// </summary>
public static class SessionEndpoints
{
    private const string Bearer = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;

    public static void MapSessions(this WebApplication app)
    {
        // ---- Self-service ("my sessions") ------------------------------------------------------------------
        //
        // Pinned to the issuer's BEARER scheme, not to bare RequireAuthorization(). The default scheme here
        // is the ASP.NET Identity application COOKIE (the login pages use it), so an unqualified
        // RequireAuthorization() ignores the SPA's bearer token entirely and challenges to the sign-in page
        // — the endpoint answers 200 with HTML instead of 401, and the ownership check below never runs.
        // Caught by UiGatingIsCosmeticTests.
        var me = app.MapGroup("/identity/me").RequireAuthorization(IdentityAdminPolicies.Authenticated);

        me.MapGet("/sessions", async (HttpContext http, SessionService sessions) =>
        {
            if (SubjectOf(http) is not { } userId) return Results.Unauthorized();
            return Results.Ok((await sessions.LiveAsync(userId, http.RequestAborted)).Select(View));
        });

        me.MapDelete("/sessions/{sessionId:guid}", async (
            HttpContext http, Guid sessionId, SessionService sessions, IdentityStoreDbContext db, IAuditClient audit) =>
        {
            if (SubjectOf(http) is not { } userId) return Results.Unauthorized();

            // Ownership is re-checked against the store. A session id is not a secret, so accepting it
            // unvalidated would let anyone sign out anyone — a denial-of-service against a colleague, and a
            // way to force a re-login that a phishing page could then harvest.
            var owned = await db.Sessions.AsNoTracking()
                .AnyAsync(s => s.SessionId == sessionId && s.UserId == userId, http.RequestAborted);
            if (!owned) return Results.Problem(statusCode: 404, title: "not-found");

            await sessions.RevokeAsync(sessionId, userId.ToString(), "self-service revoke", http.RequestAborted);
            await Emit(audit, userId.ToString(), sessionId.ToString(), "SessionRevokedBySelf");
            return Results.Ok(new { sessionId, revoked = true });
        });

        me.MapDelete("/sessions", async (HttpContext http, SessionService sessions, IAuditClient audit) =>
        {
            if (SubjectOf(http) is not { } userId) return Results.Unauthorized();

            var count = await sessions.RevokeAllAsync(userId, userId.ToString(), "self-service sign-out-everywhere", http.RequestAborted);
            await Emit(audit, userId.ToString(), userId.ToString(), "AllSessionsRevokedBySelf");
            return Results.Ok(new { revoked = count });
        });

        me.MapGet("/login-history", async (HttpContext http, SessionService sessions, int? take) =>
        {
            if (SubjectOf(http) is not { } userId) return Results.Unauthorized();
            return Results.Ok((await sessions.RecentAttemptsAsync(userId, take ?? 50, http.RequestAborted)).Select(View));
        });

        // ---- Administrative ---------------------------------------------------------------------------------
        var admin = app.MapGroup("/identity/admin/users/{id:guid}")
            .RequireAuthorization(IdentityAdminPolicies.Admin);

        admin.MapGet("/sessions", async (HttpContext http, Guid id, SessionService sessions) =>
        {
            var err = await Guard(http, "admin:read");
            return err ?? Results.Ok((await sessions.LiveAsync(id, http.RequestAborted)).Select(View));
        });

        admin.MapDelete("/sessions", async (
            HttpContext http, Guid id, SessionService sessions, IAuditClient audit) =>
        {
            var err = await Guard(http, "admin:write");
            if (err is not null) return err;

            // The off-boarding path. It fails CLOSED inside SessionService (A6): if this cannot be persisted
            // the administrator gets an error rather than a false confirmation, because someone who believes
            // the access is gone will close the incident and stop looking.
            var count = await sessions.RevokeAllAsync(id, ActorOf(http), "administrative revoke", http.RequestAborted);
            await Emit(audit, ActorOf(http), id.ToString(), "AllSessionsRevokedByAdmin");
            return Results.Ok(new { userId = id, revoked = count });
        });

        // 21.6 — revoke ONE session administratively.
        //
        // Until now an administrator's only option was the revoke-all above. That is the right tool for
        // off-boarding and the wrong one for "this one device looks stolen": signing someone out of every
        // device to kill one of them is a clinical interruption, and the cost of it means the safe action
        // gets postponed. Same fail-closed persistence as revoke-all — a revoke nobody can confirm must not
        // report success.
        admin.MapDelete("/sessions/{sessionId:guid}", async (
            HttpContext http, Guid id, Guid sessionId,
            SessionService sessions, IdentityStoreDbContext db, IAuditClient audit) =>
        {
            var err = await Guard(http, "admin:write");
            if (err is not null) return err;

            // The session must belong to the user in the route. Without this the id in the path would be
            // decoration and any session could be killed through any user's URL — the same reasoning as the
            // self-service ownership check above, which exists because a session id is not a secret.
            var owned = await db.Sessions.AsNoTracking()
                .AnyAsync(s => s.SessionId == sessionId && s.UserId == id, http.RequestAborted);
            if (!owned) return Results.Problem(statusCode: 404, title: "not-found");

            await sessions.RevokeAsync(sessionId, ActorOf(http), "administrative revoke (single session)", http.RequestAborted);
            await Emit(audit, ActorOf(http), sessionId.ToString(), "SessionRevokedByAdmin");
            return Results.Ok(new { userId = id, sessionId, revoked = true });
        });

        admin.MapGet("/login-history", async (HttpContext http, Guid id, SessionService sessions, int? take) =>
        {
            var err = await Guard(http, "admin:read");
            return err ?? Results.Ok((await sessions.RecentAttemptsAsync(id, take ?? 50, http.RequestAborted)).Select(View));
        });
    }

    /// <summary>Min-necessary session view: enough for a person to RECOGNISE a device, nothing more.</summary>
    private static object View(UserSession s) => new
    {
        sessionId = s.SessionId,
        membershipId = s.MembershipId,
        userAgent = s.UserAgent,
        // The address is shown because "signed in from somewhere I have never been" is the signal a person
        // acts on. It is never used to authenticate anything.
        ipAddress = s.IpAddress?.ToString(),
        createdAt = s.CreatedAt,
        lastSeenAt = s.LastSeenAt,
    };

    /// <summary>Min-necessary attempt view. The coarse failure reason travels as stored — it does not
    /// distinguish an unknown username from a wrong password, so it cannot become an enumeration oracle.</summary>
    private static object View(LoginAttempt a) => new
    {
        attemptedAt = a.AttemptedAt,
        succeeded = a.Succeeded,
        failureReason = a.FailureReason,
        ipAddress = a.IpAddress?.ToString(),
        userAgent = a.UserAgent,
    };

    private static Guid? SubjectOf(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(Claims.Subject), out var id) ? id : null;

    private static string ActorOf(HttpContext http) => http.User.FindFirstValue(Claims.Subject) ?? "admin";

    /// <summary>Per-action scope + MFA, matching AdminEndpoints' layer-two guard.</summary>
    private static async Task<IResult?> Guard(HttpContext http, string scope)
    {
        var auth = await http.AuthenticateAsync(Bearer);
        if (!auth.Succeeded || auth.Principal is null)
            return Results.Problem(statusCode: 401, title: "unauthenticated");

        var p = auth.Principal;
        if (!p.HasScope(scope))
            return Results.Problem(statusCode: 403, title: "insufficient-scope", detail: $"requires {scope}");

        if (!MfaEvaluator.IsSatisfied(p.GetClaim(HbmpClaimTypes.Acr), p.GetClaims(AccountPages.AmrClaim)))
            return Results.Problem(statusCode: 403, title: "mfa-required",
                detail: "administrative session actions require a step-up (MFA) session");

        return null;
    }

    private static async Task Emit(IAuditClient audit, string actor, string entityId, string outcome) =>
        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "identity.user_session", EntityId = entityId, Action = AuditAction.SoftDelete,
            ActorUserId = actor, DecisionOutcome = outcome,
        });
}
