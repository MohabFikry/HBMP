using System.Text;
using System.Text.Encodings.Web;
using Mersal.Audit.Client;
using Mersal.Email;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// Self-service password reset (ADR-0036 §6, phase 28.6).
///
/// <para>
/// ============================================================================================================
/// WHAT DID NOT EXIST BEFORE THIS
/// ============================================================================================================
/// Anything. There was no forgot-password endpoint, no reset screen and no email template. What existed was an
/// administrator typing a new password into <c>POST /identity/admin/users/{id}/reset-password</c> on somebody
/// else's behalf — which means the administrator knows the password, and there is no moment at which only its
/// owner does. 28.7 ends that; this is the half that gives people a way to help themselves.
/// </para>
///
/// <para>
/// ============================================================================================================
/// THE TOKEN COSTS NOTHING TO STORE, BECAUSE IT IS NOT STORED
/// ============================================================================================================
/// ASP.NET Identity's reset token is a data-protector token bound to the user's SECURITY STAMP.
/// <c>ResetPasswordAsync</c> rotates that stamp, so every token issued before it — including a second one, if
/// two were requested — stops verifying at the instant the password changes. Single-use for free, no table,
/// no sweeper, no race between two tabs, and no migration.
/// </para>
/// <para>
/// A hand-rolled token table would have needed its own expiry, its own single-use enforcement and its own
/// cleanup, and would have got one of the three wrong.
/// </para>
/// </summary>
public static class PasswordResetEndpoints
{
    /// <summary>The audit event names, so a reset is visible in the same chain as every other account change.</summary>
    private const string RequestedEvent = "PasswordResetRequested";
    private const string CompletedEvent = "PasswordResetCompleted";

    public static void MapPasswordReset(this WebApplication app)
    {
        var group = app.MapGroup("/connect/password");

        // ---- "I've forgotten it" ---------------------------------------------------------------------------
        group.MapPost("/forgot", async (
            HttpContext http, ForgotRequest req, IAntiforgery antiforgery,
            UserManager<ApplicationUser> users, IEmailSender email, SessionService sessions,
            IAuditClient audit, IConfiguration config, ILoggerFactory logs) =>
        {
            if (await Forged(http, antiforgery) is { } refusal) return refusal;

            // ── IT REFUSES TO LIE ABOUT DELIVERY ────────────────────────────────────────────────────────────
            //
            // With no transport configured this answers 503 and the SPA does not offer the link at all. The
            // alternative — accept the request and say "if that account exists, we've sent you a link" — would
            // be a failed operation rendered as a clean result, on the one screen a locked-out person reaches
            // when nothing else works, and it would stay wrong forever with no error anywhere.
            //
            // A capability that cannot work is ABSENT, not broken and pretending.
            if (!email.IsConfigured)
            {
                logs.CreateLogger(typeof(PasswordResetEndpoints)).LogError(
                    "A password reset was requested but no email transport is configured, so nothing can be "
                    + "delivered. Set Email:Host. Refusing rather than reporting a send that cannot happen.");
                return Results.Problem(
                    title: "Password reset is not available.",
                    detail: "No email transport is configured for this deployment.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var user = await users.FindByNameAsync(req.Username ?? "");

            // ── ALWAYS 202, WHATEVER HAPPENED ───────────────────────────────────────────────────────────────
            //
            // Unknown username, deactivated account, no email on file: all answer identically. Anything else
            // makes this endpoint a free account-existence oracle that needs no credentials at all — strictly
            // worse than the login form, which at least costs an attempt against a lockout counter.
            //
            // The FAILURE paths still record, so an operator can see resets being requested for accounts that
            // do not exist. The caller learns nothing; the audit chain learns everything.
            if (user is not null && user.IsActive && !string.IsNullOrWhiteSpace(user.Email))
            {
                try
                {
                    await SendResetLinkAsync(users, email, config, user, req.Lang, http.RequestAborted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The SEND failed, which is not the same as the account not existing — and the caller
                    // must still not be able to tell those apart. Loud in the log, silent in the reply.
                    logs.CreateLogger(typeof(PasswordResetEndpoints))
                        .LogError(ex, "Password-reset email could not be sent for user {UserId}.", user.Id);
                }

                await Emit(audit, user.Id.ToString(), RequestedEvent);
            }
            else
            {
                await sessions.RecordAttemptAsync(
                    user?.Id, req.Username ?? "", false, LoginFailureReasons.BadCredentials,
                    http.Request.Headers.UserAgent.ToString(),
                    http.RequestServices.GetRequiredService<ClientAddressResolver>().ClientIp(http),
                    http.RequestAborted);
            }

            return Results.Accepted();
        }).RequireRateLimiting(IssuerRateLimits.Credential);

        // ---- "here is my new one" ---------------------------------------------------------------------------
        group.MapPost("/reset", async (
            HttpContext http, ResetRequest req, IAntiforgery antiforgery,
            UserManager<ApplicationUser> users, SessionService sessions, IAuditClient audit) =>
        {
            if (await Forged(http, antiforgery) is { } refusal) return refusal;

            var user = req.UserId is { } id ? await users.FindByIdAsync(id.ToString()) : null;

            // DECODED, not passed through. The link carries the token base64url-encoded (see ResetLink),
            // because a raw Identity token contains characters that do not survive a query string intact.
            // The first version of this endpoint forgot to undo that, so EVERY link — correctly generated,
            // correctly delivered, clicked within the window — came back "no longer valid". The symptom is
            // indistinguishable from an expired token, which is exactly why it needed a live click to find:
            // the unit tests posted a token they had never put through a URL.
            var token = DecodeToken(req.Token);

            // One answer for an unknown user, a malformed token, an expired token and a used one — except
            // that policy failures DO say why, because "your new password is too short" is advice the person
            // can act on and reveals nothing about anybody's account.
            if (user is null || !user.IsActive || string.IsNullOrEmpty(token))
                return Invalid();

            var result = await users.ResetPasswordAsync(user, token!, req.NewPassword ?? "");
            if (!result.Succeeded)
            {
                var policy = result.Errors
                    .Where(e => !e.Code.Contains("Token", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Description)
                    .ToArray();
                return policy.Length > 0
                    ? Results.Problem(title: "That password can't be used.", detail: string.Join(" ", policy),
                        statusCode: StatusCodes.Status422UnprocessableEntity)
                    : Invalid();
            }

            // ── EVERY SESSION DIES ──────────────────────────────────────────────────────────────────────────
            //
            // If the reset was requested BECAUSE the account was compromised, leaving the attacker's live
            // session running defeats the entire exercise. The security-stamp rotation above already kills
            // outstanding reset tokens; this is what reaches the sessions and refresh tokens, which the token
            // endpoint checks by IsActive and never by security stamp.
            await sessions.RevokeAllAsync(user.Id, "self:password-reset", "password reset", http.RequestAborted);

            // ── AND THE SECOND FACTOR IS NOT TOUCHED ────────────────────────────────────────────────────────
            //
            // No SetTwoFactorEnabledAsync(false), no ResetAuthenticatorKeyAsync, no recovery code consumed. If
            // a reset could clear MFA, then control of a mailbox would be a complete account-takeover
            // primitive and the second factor would be decorative on exactly the accounts worth attacking.
            //
            // A user with 2FA still meets `two_factor_required` on their next sign-in. That is correct, and
            // the screen says so BEFORE they start, so nobody resets a password expecting it to solve a lost
            // phone. A lost authenticator is answered by a recovery code, and after that by an administrator.

            // Deliberately does NOT sign anybody in. A reset link in a mailbox must not be a session.
            await Emit(audit, user.Id.ToString(), CompletedEvent);
            return Results.Ok(new { reset = true });
        }).RequireRateLimiting(IssuerRateLimits.Credential);
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    /// <summary>
    /// Mint a reset token and send the link. Shared by the self-service path and the ADMINISTRATIVE one
    /// (28.7), so the two cannot drift into issuing different links with different lifetimes — the kind of
    /// divergence that is invisible until one of them stops working.
    /// </summary>
    internal static async Task SendResetLinkAsync(
        UserManager<ApplicationUser> users, IEmailSender email, IConfiguration config,
        ApplicationUser user, string? requestedLang, CancellationToken ct)
    {
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var link = ResetLink(config, user.Id, token);
        var lang = (requestedLang ?? "en").StartsWith("ar", StringComparison.OrdinalIgnoreCase) ? "ar" : "en";
        await email.SendAsync(user.Email!, Subject(lang), HtmlBody(lang, link), TextBody(lang, link), ct);
    }

    private static IResult Invalid() => Results.Problem(
        title: "That reset link is no longer valid.",
        detail: "Reset links expire, and each one can be used only once. Request a new one.",
        statusCode: StatusCodes.Status400BadRequest);

    private static async Task<IResult?> Forged(HttpContext http, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(http);
            return null;
        }
        catch (AntiforgeryValidationException)
        {
            return Results.Problem(
                title: "The request could not be verified.",
                detail: "Fetch a fresh antiforgery token from /connect/session/antiforgery and retry.",
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>
    /// The link the person clicks — on the SPA's own origin, derived from the SAME setting as the registered
    /// redirect URI so it cannot point somewhere the application is not.
    /// </summary>
    private static string ResetLink(IConfiguration config, Guid userId, string token)
    {
        var web = config["Issuer:WebRedirectUri"] ?? "http://localhost:5173/";
        if (!web.EndsWith('/')) web += "/";
        // Base64url over the raw token: it contains characters that do not survive a query string intact, and
        // a token mangled in transit fails verification in a way that reads exactly like an expired link.
        var encoded = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        return $"{web}reset-password?u={userId}&t={encoded}";
    }

    /// <summary>Undo <see cref="ResetLink"/>'s encoding. A malformed value is not a crash — it is an invalid
    /// link, which is what the caller is told.</summary>
    public static string? DecodeToken(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(encoded));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The actor IS the subject: a self-service reset is something a person did to their own
    /// account, and attributing it to an administrator who was not involved would falsify a hash-chained
    /// record — the same rule 27.5 settled for machine decisions, from the other end.</summary>
    private static ValueTask Emit(IAuditClient audit, string subjectId, string action) =>
        audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "identity.user",
            EntityId = subjectId,
            Action = AuditAction.Update,
            ActorUserId = subjectId,
            DecisionOutcome = action,
        });

    // ---- the message ------------------------------------------------------------------------------------
    //
    // Bilingual, and it says the two things a person needs before they click: that the link is short-lived and
    // single-use, and that IF THEY DID NOT ASK FOR IT nothing has happened yet. The second sentence is what
    // makes an unexpected reset email a warning rather than a scare.

    private static string Subject(string lang) => lang == "ar"
        ? "إعادة تعيين كلمة مرور مرسال HBMP"
        : "Reset your Mersal HBMP password";

    private static string TextBody(string lang, string link) => lang == "ar"
        ? $"لإعادة تعيين كلمة المرور، افتح هذا الرابط خلال 30 دقيقة:\n{link}\n\n"
          + "يمكن استخدام الرابط مرة واحدة فقط. إذا لم تطلب ذلك، فلا حاجة لأي إجراء — لم يتغيّر شيء، "
          + "وكلمة مرورك الحالية ما زالت تعمل.\n\n"
          + "إعادة التعيين لا تُلغي التحقق بخطوتين؛ ستظل بحاجة إلى رمز المصادقة عند تسجيل الدخول."
        : $"To reset your password, open this link within 30 minutes:\n{link}\n\n"
          + "The link can be used once. If you didn't ask for this, nothing has happened — your current "
          + "password still works and no action is needed.\n\n"
          + "Resetting your password does not turn off two-step verification; you'll still need your "
          + "authenticator code to sign in.";

    private static string HtmlBody(string lang, string link)
    {
        var enc = HtmlEncoder.Default;
        var dir = lang == "ar" ? "rtl" : "ltr";
        var body = TextBody(lang, link)
            .Replace(link, "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => $"<p>{enc.Encode(p.Trim())}</p>");
        var label = lang == "ar" ? "إعادة تعيين كلمة المرور" : "Reset your password";
        return $"""
            <div dir="{dir}" style="font-family:system-ui,Segoe UI,Roboto,'Noto Naskh Arabic',sans-serif;max-width:32rem">
            {string.Join("\n", body)}
            <p><a href="{enc.Encode(link)}">{enc.Encode(label)}</a></p>
            </div>
            """;
    }

    public sealed record ForgotRequest(string? Username, string? Lang);
    public sealed record ResetRequest(Guid? UserId, string? Token, string? NewPassword);
}
