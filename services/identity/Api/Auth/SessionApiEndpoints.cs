using System.Net;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// The first-party sign-in API the SPA drives (ADR-0036 §5, phase 28.3).
///
/// <para>
/// ============================================================================================================
/// WHAT THESE ARE, AND WHAT THEY ARE NOT
/// ============================================================================================================
/// They establish the SAME ASP.NET Identity session cookie <c>POST /connect/login</c> has always set, and
/// report a <see cref="SessionStatus"/>. They mint nothing. Once the cookie is stamped, the caller runs the
/// unchanged authorization-code + PKCE flow with <c>prompt=none</c>, and every downstream property — the
/// frozen token contract, membership re-resolution, scope narrowing, MfaEvaluator, refresh rotation — is
/// untouched because none of it is reimplemented here.
/// </para>
///
/// <para>
/// ============================================================================================================
/// PARITY WITH THE FORM PATH IS THE POINT
/// ============================================================================================================
/// <c>SessionService.RecordAttemptAsync</c> and <c>OpenAsync</c> live in the form handlers. An API that
/// forgot them would not fail loudly: it would silently empty <c>/identity/me/login-history</c> — the screen
/// that exists so a person can see their account being attacked — and silently stop applying the concurrent
/// session cap. So every outcome here records, every success opens, and
/// <c>SessionApiParityTests</c> compares the two paths rather than checking either against itself.
/// </para>
///
/// <para>
/// ============================================================================================================
/// WHY THESE NEED CSRF PROTECTION AS MUCH AS THE FORMS DID
/// ============================================================================================================
/// They are cookie-authenticated, so they are CSRF-relevant. <c>SameSite=Strict</c> is the primary defence
/// and antiforgery is the secondary one, exactly as for the pages. The reasoning in
/// <c>AccountPages.AntiforgeryField</c> about enrolment is the sharpest case and carries over unchanged: a
/// forged enrolment makes the ATTACKER's authenticator the victim's second factor, on an account that was
/// already signed in and shows the victim nothing.
/// </para>
/// </summary>
public static class SessionApiEndpoints
{
    public static void MapSessionApi(this WebApplication app)
    {
        var group = app.MapGroup("/connect/session");

        // ---- the antiforgery token the SPA sends back as a header ------------------------------------------
        //
        // A GET, deliberately unauthenticated: it is fetched before anybody has signed in, which is the whole
        // point of a login CSRF token. It issues the cookie half of the pair and returns the request half.
        group.MapGet("/antiforgery", (HttpContext http, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(http);
            return Results.Ok(new { token = tokens.RequestToken ?? "" });
        });

        // ---- where am I in the sequence? --------------------------------------------------------------------
        //
        // Lets a reloaded tab find out whether the issuer session survived without re-asking for a password.
        // Answers only about the CALLER's own cookie and returns no identity beyond the step they are on.
        group.MapGet("", async (
            HttpContext http, IAntiforgery antiforgery,
            UserManager<ApplicationUser> users, MembershipService memberships) =>
        {
            var auth = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
            if (!auth.Succeeded) return Ok(http, antiforgery, new SessionStatusResponse(SessionStatus.InvalidCredentials));

            var user = await users.GetUserAsync(auth.Principal!);
            if (user is null || !user.IsActive)
                return Ok(http, antiforgery, new SessionStatusResponse(SessionStatus.InvalidCredentials));

            return Ok(http, antiforgery, await ResolveMembershipStateAsync(user, users, memberships, http.RequestAborted));
        });

        // ---- step 1: the password ---------------------------------------------------------------------------
        group.MapPost("", async (
            HttpContext http, SignInRequest req, IAntiforgery antiforgery,
            SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users,
            MembershipService memberships, SessionService sessions) =>
        {
            if (await Forged(http, antiforgery) is { } refusal) return refusal;

            var agent = http.Request.Headers.UserAgent.ToString();
            var ip = ClientIp(http);
            var username = req.Username ?? "";

            var user = await users.FindByNameAsync(username);
            if (user is null || !user.IsActive)
            {
                // Both record the SAME coarse reason as the form path does, so the distinction between "no
                // such user" and "deactivated" cannot leak into a support screen and become an enumeration
                // oracle. The RESPONSE does not distinguish them either.
                await sessions.RecordAttemptAsync(
                    user?.Id, username, false,
                    user is null ? LoginFailureReasons.BadCredentials : LoginFailureReasons.Inactive,
                    agent, ip, http.RequestAborted);
                return Ok(http, antiforgery, new SessionStatusResponse(SessionStatus.InvalidCredentials));
            }

            var result = await signIn.PasswordSignInAsync(
                user, req.Password ?? "", isPersistent: false, lockoutOnFailure: true);

            if (result.RequiresTwoFactor)
            {
                // Deliberately records NOTHING. The attempt becomes an outcome when the second factor resolves
                // it — filing a failure here would put one against every successful MFA sign-in and make the
                // history least readable for the accounts that are best protected.
                return Ok(http, antiforgery, new SessionStatusResponse(SessionStatus.TwoFactorRequired));
            }

            if (!result.Succeeded)
            {
                await sessions.RecordAttemptAsync(
                    user.Id, username, false,
                    result.IsLockedOut ? LoginFailureReasons.LockedOut : LoginFailureReasons.BadCredentials,
                    agent, ip, http.RequestAborted);

                // Lockout IS distinguished, and §5.2 argues why: telling a locked-out user their credentials
                // are invalid sends them to reset a password that was never wrong — and the reset does not
                // unlock the account, so they lose the password AND stay locked out.
                return Ok(http, antiforgery, result.IsLockedOut
                    ? new SessionStatusResponse(SessionStatus.Locked, RetryAfterSeconds: LockoutSeconds(users))
                    : new SessionStatusResponse(SessionStatus.InvalidCredentials));
            }

            await AccountPages.StampSignIn(http, signIn, user, ["pwd"], persistent: req.RememberDevice ?? false);
            await sessions.RecordAttemptAsync(user.Id, username, true, null, agent, ip, http.RequestAborted);
            await sessions.OpenAsync(user.Id, null, agent, ip, http.RequestAborted);
            return Ok(http, antiforgery, await ResolveMembershipStateAsync(user, users, memberships, http.RequestAborted));
        }).RequireRateLimiting(IssuerRateLimits.Credential);

        // ---- step 2: the second factor ----------------------------------------------------------------------
        group.MapPost("/2fa", async (
            HttpContext http, TwoFactorRequest req, IAntiforgery antiforgery,
            SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users,
            MembershipService memberships, SessionService sessions) =>
        {
            if (await Forged(http, antiforgery) is { } refusal) return refusal;

            // The half-authenticated user carried on the TwoFactorUserId cookie. Absent ⇒ there is no password
            // step to continue, and this is not a way to start one.
            var user = await signIn.GetTwoFactorAuthenticationUserAsync();
            if (user is null) return Ok(http, antiforgery, new SessionStatusResponse(SessionStatus.InvalidCredentials));

            var agent = http.Request.Headers.UserAgent.ToString();
            var ip = ClientIp(http);
            var code = (req.Code ?? "").Replace(" ", "").Replace("-", "");

            var ok = req.Recovery is true
                ? (await signIn.TwoFactorRecoveryCodeSignInAsync(code)).Succeeded
                : (await signIn.TwoFactorAuthenticatorSignInAsync(code, isPersistent: false, rememberClient: false))
                    .Succeeded;

            if (!ok)
            {
                await sessions.RecordAttemptAsync(
                    user.Id, user.UserName ?? "", false, LoginFailureReasons.TwoFactorFailed,
                    agent, ip, http.RequestAborted);
                return Ok(http, antiforgery, new SessionStatusResponse(SessionStatus.InvalidCredentials));
            }

            await AccountPages.StampSignIn(http, signIn, user, ["pwd", "otp"]);
            await sessions.RecordAttemptAsync(user.Id, user.UserName ?? "", true, null, agent, ip, http.RequestAborted);
            await sessions.OpenAsync(user.Id, null, agent, ip, http.RequestAborted);
            return Ok(http, antiforgery, await ResolveMembershipStateAsync(user, users, memberships, http.RequestAborted));
        }).RequireRateLimiting(IssuerRateLimits.Credential);

        // ---- step 3: which organization ---------------------------------------------------------------------
        group.MapPost("/membership", async (
            HttpContext http, MembershipRequest req, IAntiforgery antiforgery,
            SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users,
            MembershipService memberships) =>
        {
            if (await Forged(http, antiforgery) is { } refusal) return refusal;

            var auth = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
            if (!auth.Succeeded) return Ok(http, antiforgery, new SessionStatusResponse(SessionStatus.InvalidCredentials));

            var user = await users.GetUserAsync(auth.Principal!);
            if (user is null || !user.IsActive) return Ok(http, antiforgery, new SessionStatusResponse(SessionStatus.InvalidCredentials));

            // RE-RESOLVED, never trusted. A membership id is not a secret and this body is caller-controlled,
            // so an unvalidated value here would be a direct path into another organization's tenant — the
            // same reasoning the form chooser records.
            var chosen = await memberships.ResolveAsync(user.Id, req.MembershipId, http.RequestAborted);
            if (chosen is null)
                return Ok(http, antiforgery, await ResolveMembershipStateAsync(user, users, memberships, http.RequestAborted));

            // Re-issue carrying the selection ALONGSIDE the factors already performed. Re-signing in without
            // them would drop amr=otp and silently demote a two-factor session to a single-factor one.
            var amr = auth.Principal!.FindAll(AccountPages.AmrClaim).Select(c => c.Value).ToArray();
            await AccountPages.StampSignIn(http, signIn, user, amr.Length > 0 ? amr : ["pwd"], chosen.MembershipId);

            return Ok(http, antiforgery, new SessionStatusResponse(
                SessionStatus.Authenticated,
                TwoFactorEnrolled: await users.GetTwoFactorEnabledAsync(user)));
        }).RequireRateLimiting(IssuerRateLimits.Credential);

        // ---- enrolment, for a signed-in caller ---------------------------------------------------------------
        //
        // In scope here rather than deferred because the SPA cannot send a user to the server-rendered
        // enrolment page without re-creating exactly the "moves to another platform" problem this ADR removes.
        group.MapGet("/authenticator", async (HttpContext http, UserManager<ApplicationUser> users) =>
        {
            var auth = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
            if (!auth.Succeeded) return Results.Unauthorized();
            var user = await users.GetUserAsync(auth.Principal!);
            if (user is null || !user.IsActive) return Results.Unauthorized();

            var key = await users.GetAuthenticatorKeyAsync(user);
            if (string.IsNullOrEmpty(key))
            {
                await users.ResetAuthenticatorKeyAsync(user);
                key = await users.GetAuthenticatorKeyAsync(user);
            }
            var account = Uri.EscapeDataString(user.UserName ?? "");
            return Results.Ok(new
            {
                key,
                otpauthUri = $"otpauth://totp/Mersal%20HBMP:{account}?secret={key}&issuer=Mersal%20HBMP&digits=6",
            });
        });

        group.MapPost("/authenticator", async (
            HttpContext http, TwoFactorRequest req, IAntiforgery antiforgery,
            SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users) =>
        {
            if (await Forged(http, antiforgery) is { } refusal) return refusal;

            var auth = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
            if (!auth.Succeeded) return Results.Unauthorized();
            var user = await users.GetUserAsync(auth.Principal!);
            if (user is null || !user.IsActive) return Results.Unauthorized();

            var code = (req.Code ?? "").Replace(" ", "").Replace("-", "");
            var valid = await users.VerifyTwoFactorTokenAsync(
                user, users.Options.Tokens.AuthenticatorTokenProvider, code);
            if (!valid) return Ok(http, antiforgery, new SessionStatusResponse(SessionStatus.InvalidCredentials));

            await users.SetTwoFactorEnabledAsync(user, true);
            var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            // The enrolling session has now proven a second factor.
            await AccountPages.StampSignIn(http, signIn, user, ["pwd", "otp"]);
            return Results.Ok(new { recoveryCodes = codes?.ToArray() ?? [] });
        }).RequireRateLimiting(IssuerRateLimits.Credential);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    /// <summary>
    /// Authenticated — now, can this identity act anywhere, and if so where?
    ///
    /// <para>
    /// Mirrors what <c>/connect/authorize</c> does, so that a caller who reaches
    /// <see cref="SessionStatus.Authenticated"/> here does not then meet a chooser redirect there. One
    /// selectable membership auto-resolves and needs no stamping — exactly as the form path relies on.
    /// </para>
    /// </summary>
    private static async Task<SessionStatusResponse> ResolveMembershipStateAsync(
        ApplicationUser user, UserManager<ApplicationUser> users, MembershipService memberships,
        CancellationToken ct)
    {
        var options = await memberships.SelectableAsync(user.Id, ct);
        if (options.Count == 0) return new SessionStatusResponse(SessionStatus.NoMembership);
        if (options.Count > 1)
            return new SessionStatusResponse(SessionStatus.MembershipSelectionRequired, Memberships: options);

        return new SessionStatusResponse(
            SessionStatus.Authenticated,
            TwoFactorEnrolled: await users.GetTwoFactorEnabledAsync(user));
    }

    /// <summary>Validate the antiforgery pair, or refuse. Returns null when the request is genuine.</summary>
    private static async Task<IResult?> Forged(HttpContext http, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(http);
            return null;
        }
        catch (AntiforgeryValidationException)
        {
            // 400, not a status. A missing or stale token is a fact about the REQUEST, and answering it with
            // `invalid_credentials` would tell a user with a correct password that it was wrong — while
            // handing an attacker probing CSRF exactly the same reply as a failed guess.
            return Results.Problem(
                title: "The request could not be verified.",
                detail: "Fetch a fresh antiforgery token from /connect/session/antiforgery and retry.",
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>How long a lockout lasts, from the configured policy rather than a number written here — the
    /// two would drift and the user-facing one is the one that would be wrong.</summary>
    private static int LockoutSeconds(UserManager<ApplicationUser> users) =>
        (int)Math.Max(1, users.Options.Lockout.DefaultLockoutTimeSpan.TotalSeconds);

    /// <summary>The CLIENT's address, through the proxy chain — the SAME resolver the rate limiter uses
    /// (phase 28.1). Recording the gateway's address in every login-history row would make the screen that
    /// exists to show a person where their account is being accessed from show them one constant; and a
    /// second copy of the chain walk here would have been the 27.6 shape again — the first draft of it
    /// trusted the forwarded header unconditionally, which would have let a direct caller write their own
    /// address into somebody else's history.</summary>
    private static IPAddress? ClientIp(HttpContext http) =>
        http.RequestServices.GetRequiredService<ClientAddressResolver>().ClientIp(http);

    /// <summary>Reply, with a FRESH antiforgery token for the next step in the sequence — see
    /// <see cref="SessionStatusResponse.Csrf"/> for why this is not optional.</summary>
    private static IResult Ok(HttpContext http, IAntiforgery antiforgery, SessionStatusResponse response) =>
        Results.Ok(response with { Csrf = antiforgery.GetAndStoreTokens(http).RequestToken });

    public sealed record SignInRequest(string? Username, string? Password, bool? RememberDevice);
    public sealed record TwoFactorRequest(string? Code, bool? Recovery);
    public sealed record MembershipRequest(Guid MembershipId);
}
