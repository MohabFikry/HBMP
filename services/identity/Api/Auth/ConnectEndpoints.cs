using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.AspNetCore; // OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest(HttpContext)
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// The OIDC endpoints (17.2): authorize, token, userinfo, logout, and a MINIMAL password sign-in the
/// authorize flow challenges to. The polished login page + TOTP 2FA + recovery + step-up replace the minimal
/// login in 17.3. All token minting goes through <see cref="TokenPrincipalFactory"/> so the frozen contract
/// is emitted identically on every grant.
/// </summary>
public static class ConnectEndpoints
{
    private const string Scheme = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;

    public static void MapConnect(this WebApplication app)
    {
        // ---- Authorization endpoint (auth-code + PKCE) ------------------------------------------------------
        app.MapMethods("/connect/authorize", ["GET", "POST"], async (
            HttpContext http, UserManager<ApplicationUser> users, UserClaimsService claims,
            TokenPrincipalFactory factory, MembershipService memberships) =>
        {
            var request = http.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

            // 28.3 — prompt=none means "answer, do NOT interact" (OIDC Core §3.1.2.1). This is how the SPA
            // completes a sign-in it has already driven through /connect/session: the cookie is set, so
            // authorize returns a code without a single page being rendered.
            //
            // It is also the LOOP-BREAKER. The SPA does not use the server-rendered login, so an authorize it
            // cannot satisfy has to terminate in an error the SPA can read. Challenging would redirect it to
            // the very page this ADR exists to stop showing, and it would follow that redirect forever.
            var silent = request.HasPrompt(Prompts.None);

            var auth = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
            if (!auth.Succeeded)
            {
                if (silent) return Forbid(Errors.LoginRequired, "No active issuer session.");
                // Not signed in → bounce to the (minimal) login, returning here afterwards.
                return Results.Challenge(
                    new AuthenticationProperties { RedirectUri = http.Request.PathBase + http.Request.Path + http.Request.QueryString },
                    [IdentityConstants.ApplicationScheme]);
            }

            var user = await users.GetUserAsync(auth.Principal!);
            if (user is null || !user.IsActive)
                return Results.Forbid(properties: null, [IdentityConstants.ApplicationScheme]);

            // 21.1c — authority comes from the ACTIVE MEMBERSHIP (invariant 1). The selection stamped on the
            // sign-in cookie is re-validated here rather than trusted: a cookie outlives the membership it
            // names, so one suspended mid-session stops resolving at the next authorize.
            var selected = MembershipIdFrom(auth.Principal);
            var membership = await memberships.ResolveAsync(user.Id, selected, http.RequestAborted);
            if (membership is null)
            {
                var options = await memberships.SelectableAsync(user.Id, http.RequestAborted);
                // None selectable ⇒ the identity exists but may act nowhere. Refuse; do not fall back to the
                // identity-level roles, which is exactly the blended principal this phase removes.
                if (options.Count == 0)
                    return Forbid(Errors.AccessDenied, "This account has no active membership in any organization.");

                // Under prompt=none the chooser is an INTERACTION, and the spec's word for "I would have had
                // to ask you something" is interaction_required — distinct from login_required, because the
                // remedy is different: the caller must pick an organization, not sign in again. Collapsing
                // the two would send a signed-in user back to a password prompt to answer a question about
                // which tenant they are working in.
                if (silent) return Forbid(Errors.InteractionRequired, "A membership must be selected.");

                var back = http.Request.PathBase + http.Request.Path + http.Request.QueryString;
                return Results.Redirect($"/connect/select-membership?returnUrl={Uri.EscapeDataString(back)}");
            }

            var facts = await claims.ForAsync(user, membership, http.RequestAborted);
            // The factors performed were stamped on the application cookie at sign-in (pwd, and otp when 2FA
            // was completed); carry them onto the token so MfaEvaluator can gate protected scopes.
            var amr = auth.Principal!.FindAll(AccountPages.AmrClaim).Select(c => c.Value).ToArray();
            var principal = factory.ForUser(facts, request.GetScopes(), amr.Length > 0 ? amr : ["pwd"]);
            // 18.B3 (S5): refuse at AUTHORIZE too, so the user is told now rather than being redirected back
            // with a code that cannot be exchanged.
            if (principal is null)
                return Forbid(Errors.InvalidScope, "None of the requested scopes are granted to this account.");
            return Results.SignIn(principal, properties: null, Scheme);
        });

        // ---- Token endpoint (authorization_code / refresh_token / client_credentials) -----------------------
        app.MapPost("/connect/token", async (
            HttpContext http, UserManager<ApplicationUser> users, UserClaimsService claims,
            TokenPrincipalFactory factory, MembershipService memberships) =>
        {
            var request = http.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

            if (request.IsClientCredentialsGrantType())
            {
                // Machine-to-machine: subject is the client; scopes are the (permission-checked) requested set.
                var identity = new System.Security.Claims.ClaimsIdentity("mersal-issuer", Claims.Name, TokenPrincipalFactory.RolesClaim);
                identity.SetClaim(Claims.Subject, request.ClientId!);
                var machine = new System.Security.Claims.ClaimsPrincipal(identity);
                machine.SetScopes(request.GetScopes());
                machine.SetResources(IdentityContract.ApiResource);
                machine.SetDestinations(TokenPrincipalFactory.Destinations);
                return Results.SignIn(machine, properties: null, Scheme);
            }

            if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
            {
                var stored = await http.AuthenticateAsync(Scheme);
                if (stored.Principal is null)
                    return Forbid(Errors.InvalidGrant, "The authorization is no longer valid.");

                var userId = stored.Principal.GetClaim(Claims.Subject);
                var user = userId is null ? null : await users.FindByIdAsync(userId);
                if (user is null || !user.IsActive)
                    return Forbid(Errors.InvalidGrant, "The account no longer exists or is disabled.");

                // Re-mint fresh roles/scopes (they may have changed since the code was issued), constrained to
                // the scopes originally granted; carry the amr recorded at authorize time.
                //
                // 21.1c — re-resolve the MEMBERSHIP too, from the id carried on the stored grant. This is the
                // re-resolution seam ADR-0021 §3 relies on: a membership suspended or ended after the code was
                // issued stops minting tokens at the next exchange, within the access-token TTL. A refresh
                // must never widen authority, so a grant whose membership no longer resolves is refused
                // outright rather than falling back to the identity-level roles.
                var storedMembership = MembershipIdFrom(stored.Principal);
                UserTokenFacts facts;
                if (storedMembership is { } sm)
                {
                    var membership = await memberships.ResolveAsync(user.Id, sm, http.RequestAborted);
                    if (membership is null)
                        return Forbid(Errors.InvalidGrant, "The membership this authorization was issued for is no longer active.");
                    facts = await claims.ForAsync(user, membership, http.RequestAborted);
                }
                else
                {
                    // Legacy grant issued before 21.1c (no membership_id). Keeps existing sessions alive
                    // across the deploy; disappears with the contract migration that drops user_role.
                    facts = await claims.ForAsync(user, http.RequestAborted);
                }

                var amr = stored.Principal.GetClaims(TokenPrincipalFactory.AmrClaim);
                var principal = factory.ForUser(facts, stored.Principal.GetScopes(),
                    amr.Length > 0 ? amr : ["pwd"]);
                // 18.B3 (S5): no grantable scope ⇒ invalid_scope. This used to fall back to the user's ENTIRE
                // entitlement, so the narrowest request produced the broadest token.
                if (principal is null)
                    return Forbid(Errors.InvalidScope, "None of the requested scopes are granted to this account.");
                return Results.SignIn(principal, properties: null, Scheme);
            }

            return Forbid(Errors.UnsupportedGrantType, "The specified grant type is not supported.");
        }).RequireRateLimiting(IssuerRateLimits.Token);   // 18.B3 (S9)

        // ---- UserInfo -------------------------------------------------------------------------------------
        app.MapMethods("/connect/userinfo", ["GET", "POST"], async (HttpContext http, UserManager<ApplicationUser> users) =>
        {
            var principal = (await http.AuthenticateAsync(Scheme)).Principal;
            var userId = principal?.GetClaim(Claims.Subject);
            var user = userId is null ? null : await users.FindByIdAsync(userId);
            if (user is null) return Results.Challenge(authenticationSchemes: [Scheme]);

            return Results.Ok(new Dictionary<string, object?>
            {
                [Claims.Subject] = user.Id.ToString(),
                [Claims.Name] = user.DisplayName,
                ["tenant_id"] = user.TenantId,
                ["provider_id"] = user.ProviderId?.ToString(),
            });
        }).RequireAuthorization();

        // ---- Current entitlement (28.11) ------------------------------------------------------------------
        //
        // "Which scopes would this caller be granted if they authorised RIGHT NOW?"
        //
        // ============================================================================================================
        // WHY THE SPA CANNOT ANSWER THIS FOR ITSELF
        // ============================================================================================================
        // A token's scopes are fixed at authorisation, and the refresh grant above deliberately constrains its
        // re-mint to the scopes on the stored grant — a refresh must never widen authority. So when an
        // administrator adds a scope to a role, every live session keeps the narrower token until its refresh
        // token expires, and every screen needing the new scope collects a 403 in the meantime.
        //
        // The SPA cannot detect that by reading its own token. It knows what it ASKED for and what it RECEIVED,
        // but the gap between them is normally just least privilege working — a reception token legitimately
        // carries 15 of the 80 scopes the application requests. Treating that gap as staleness is precisely the
        // bug this endpoint replaces: the client-side guard that did so was false for every user in the system
        // and signed people out on every page load. Only the issuer knows which of the two it is.
        //
        // So the issuer says. The caller intersects the answer with its own request list and re-authorises when
        // something it needs is missing — the policy decision stays with the client, because only the client
        // knows which scopes it intends to use.
        //
        // ============================================================================================================
        // WHAT IT DISCLOSES, AND WHAT IT IS NOT
        // ============================================================================================================
        // A caller's OWN entitlement, to a caller already holding a token that exercises it. Minimum-necessary
        // is satisfied by construction: the subject and membership come from the bearer token, never from the
        // request, so there is no parameter with which to ask about somebody else.
        //
        // It is NOT a revocation channel. A membership that has been suspended fails here with 403, but the
        // control that ENDS such a session is the refresh grant above, which refuses within the access token's
        // 5-minute lifetime. A client is free to ignore this endpoint entirely; nothing about authority
        // enforcement depends on it being called.
        //
        // Not audited, deliberately. It reads no PHI and no other person's data, and it is called once per page
        // load — an audit row per reload would bury the disclosure events the trail exists to make findable.
        app.MapGet("/connect/entitlement", async (
            HttpContext http, UserManager<ApplicationUser> users, UserClaimsService claims,
            MembershipService memberships) =>
        {
            // Authenticated explicitly as well as by the route policy — the same two-layer rule the admin
            // surface follows, because a control that depends on a route group being wired correctly is one
            // careless edit from silence.
            var auth = await http.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
            if (!auth.Succeeded || auth.Principal is null)
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "unauthenticated");

            var userId = auth.Principal.GetClaim(Claims.Subject);
            var user = userId is null ? null : await users.FindByIdAsync(userId);
            if (user is null || !user.IsActive)
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "unauthenticated");

            // Resolved exactly as the refresh grant resolves it, and for the same reason: authority lives on the
            // MEMBERSHIP (invariant 1). Answering from the identity-level roles when a membership is present
            // would report an entitlement no token minted for this session could ever carry, and the client
            // would re-authorise in a loop chasing scopes it cannot be granted.
            var membershipId = MembershipIdFrom(auth.Principal);
            UserTokenFacts facts;
            if (membershipId is { } mid)
            {
                var membership = await memberships.ResolveAsync(user.Id, mid, http.RequestAborted);
                if (membership is null)
                    return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "membership-inactive",
                        detail: "The membership this session was issued for is no longer active.");
                facts = await claims.ForAsync(user, membership, http.RequestAborted);
            }
            else
            {
                // The membership-less legacy grant. Kept in step with the refresh branch above; both disappear
                // with the contract migration that drops user_role.
                facts = await claims.ForAsync(user, http.RequestAborted);
            }

            // Sorted, so a client comparing sets is not comparing dictionary iteration order.
            return Results.Ok(new EntitlementResponse(
                [.. facts.Scopes.OrderBy(s => s, StringComparer.Ordinal)]));
        })
        .RequireAuthorization(IdentityAdminPolicies.Authenticated)
        .RequireRateLimiting(IssuerRateLimits.Token);

        // ---- Password sign-in (17.3 login UI); routes to TOTP when the account has 2FA enabled -------------
        app.MapGet("/connect/login", (HttpContext http, IAntiforgery antiforgery, string? returnUrl) =>
            Results.Content(AccountPages.LoginPage(LangOf(http), returnUrl,
                AccountPages.AntiforgeryField(antiforgery, http)), "text/html"));

        app.MapPost("/connect/login", async (
            HttpContext http, [FromForm] string username, [FromForm] string password, [FromForm] string? returnUrl,
            IAntiforgery antiforgery, SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users,
            SessionService sessions) =>
        {
            var lang = LangOf(http);
            var agent = http.Request.Headers.UserAgent.ToString();
            var ip = http.Connection.RemoteIpAddress;

            // 21.5 — every outcome is recorded, failures included. A history containing only the successes
            // cannot show anyone that their account is being attacked. Both the "no such user" and the
            // "wrong password" paths record the SAME coarse reason, so the distinction cannot leak into a
            // support screen and become a user-enumeration oracle.
            IResult Failed() => Results.Content(AccountPages.LoginPage(lang, returnUrl,
                AccountPages.AntiforgeryField(antiforgery, http), error: true), "text/html");

            var user = await users.FindByNameAsync(username);
            if (user is null || !user.IsActive)
            {
                await sessions.RecordAttemptAsync(
                    user?.Id, username, false,
                    user is null ? LoginFailureReasons.BadCredentials : LoginFailureReasons.Inactive,
                    agent, ip, http.RequestAborted);
                return Failed();
            }

            var result = await signIn.PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true);
            if (result.RequiresTwoFactor)
            {
                // Not an outcome yet — the attempt is recorded when the second factor resolves it.
                var q = string.IsNullOrEmpty(returnUrl) ? "" : $"?returnUrl={Uri.EscapeDataString(AccountPages.SafeReturn(returnUrl))}";
                return Results.Redirect($"/connect/2fa{q}");
            }
            if (!result.Succeeded)
            {
                await sessions.RecordAttemptAsync(
                    user.Id, username, false,
                    result.IsLockedOut ? LoginFailureReasons.LockedOut : LoginFailureReasons.BadCredentials,
                    agent, ip, http.RequestAborted);
                return Failed();
            }

            // Single-factor success: stamp amr=pwd. Protected scopes will still be denied downstream until the
            // user enrols a second factor (/connect/enroll-2fa) — that is the MFA gate (C3) on the new issuer.
            await AccountPages.StampSignIn(http, signIn, user, ["pwd"]);
            await sessions.RecordAttemptAsync(user.Id, username, true, null, agent, ip, http.RequestAborted);
            // 21.5 — opening the session also applies the concurrent cap, revoking the oldest.
            await sessions.OpenAsync(user.Id, null, agent, ip, http.RequestAborted);
            return Results.Redirect(AccountPages.SafeReturn(returnUrl));
        }).RequireRateLimiting(IssuerRateLimits.Credential);

        // ---- Membership chooser (21.1c) --------------------------------------------------------------------
        //
        // Reached only when an identity holds MORE THAN ONE selectable membership; one auto-selects in
        // ResolveAsync and never lands here. The choice is stamped onto the sign-in cookie and re-validated on
        // every authorize — the cookie records what was picked, it does not grant it.
        app.MapGet("/connect/select-membership", async (
            HttpContext http, IAntiforgery antiforgery, string? returnUrl,
            UserManager<ApplicationUser> users, MembershipService memberships) =>
        {
            var auth = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
            if (!auth.Succeeded) return Results.Challenge(
                new AuthenticationProperties { RedirectUri = "/connect/select-membership" }, [IdentityConstants.ApplicationScheme]);

            var user = await users.GetUserAsync(auth.Principal!);
            if (user is null || !user.IsActive) return Results.Forbid(properties: null, [IdentityConstants.ApplicationScheme]);

            var options = await memberships.SelectableAsync(user.Id, http.RequestAborted);
            if (options.Count == 0)
                return Forbid(Errors.AccessDenied, "This account has no active membership in any organization.");

            // Deliberately NO "exactly one ⇒ redirect straight back" shortcut. Authorize only sends people here
            // when resolution failed, and one way that happens is a cookie naming a membership that has since
            // been suspended while ONE other remains selectable. Redirecting back would hand authorize the same
            // stale cookie, which fails to resolve again, which redirects here again — an infinite loop. The
            // single option is rendered instead, so the POST restamps the cookie and the session moves forward.
            // It also means nobody is moved to a different organization without seeing that it happened.
            return Results.Content(AccountPages.MembershipChooserPage(
                LangOf(http), [.. options.Select(o => (o.MembershipId, o.TenantId, o.Roles))],
                returnUrl, AccountPages.AntiforgeryField(antiforgery, http)), "text/html");
        });

        app.MapPost("/connect/select-membership", async (
            HttpContext http, [FromForm] Guid membershipId, [FromForm] string? returnUrl,
            IAntiforgery antiforgery, SignInManager<ApplicationUser> signIn,
            UserManager<ApplicationUser> users, MembershipService memberships) =>
        {
            var auth = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
            if (!auth.Succeeded) return Results.Challenge(properties: null, [IdentityConstants.ApplicationScheme]);

            var user = await users.GetUserAsync(auth.Principal!);
            if (user is null || !user.IsActive) return Results.Forbid(properties: null, [IdentityConstants.ApplicationScheme]);

            // Validate the POSTed id against what this identity may actually select. A membership id is not a
            // secret and the form is user-controlled, so an unvalidated value here would be a direct path into
            // another organization's tenant — re-resolve rather than trust.
            var chosen = await memberships.ResolveAsync(user.Id, membershipId, http.RequestAborted);
            if (chosen is null)
            {
                var options = await memberships.SelectableAsync(user.Id, http.RequestAborted);
                if (options.Count == 0)
                    return Forbid(Errors.AccessDenied, "This account has no active membership in any organization.");
                return Results.Content(AccountPages.MembershipChooserPage(
                    LangOf(http), [.. options.Select(o => (o.MembershipId, o.TenantId, o.Roles))],
                    returnUrl, AccountPages.AntiforgeryField(antiforgery, http), error: true), "text/html");
            }

            // Re-issue the cookie carrying the selection alongside the amr already recorded, so the factors
            // performed are not lost by re-signing in.
            var amr = auth.Principal!.FindAll(AccountPages.AmrClaim).Select(c => c.Value).ToArray();
            await AccountPages.StampSignIn(http, signIn, user, amr.Length > 0 ? amr : ["pwd"], chosen.MembershipId);

            return Results.Redirect(AccountPages.SafeReturn(returnUrl));
        });

        // GET and POST both: RP-initiated logout is a BROWSER NAVIGATION (the OIDC end-session endpoint
        // is a front-channel GET), and this was mapped POST-only — so the SPA's sign-out redirect 404ed,
        // the SSO cookie survived, and the next sign-in silently re-authenticated as the same user until
        // the person cleared cookies by hand (QA follow-up). OpenIddict already validates the logout
        // request on either verb; only the passthrough mapping was missing the GET.
        app.MapMethods("/connect/logout", ["GET", "POST"], async (HttpContext http, SignInManager<ApplicationUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [Scheme]);
        });
    }

    private static IResult Forbid(string error, string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }),
        [Scheme]);

    private static string LangOf(HttpContext http) => http.Request.Query["lang"] == "ar" ? "ar" : "en";

    /// <summary>The membership selection carried on a sign-in cookie or a stored grant, if any and if it
    /// parses. A malformed value reads as "no selection" and re-runs resolution — never as a wildcard.</summary>
    private static Guid? MembershipIdFrom(System.Security.Claims.ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirst(TokenPrincipalFactory.MembershipClaim)?.Value, out var id) ? id : null;
}

/// <summary>
/// The answer from <c>/connect/entitlement</c>: every platform scope the caller would be granted on a fresh
/// authorisation, sorted.
///
/// A named record rather than an anonymous object so the shape is greppable from the client that consumes it —
/// and so a future field cannot be added by accident in a lambda nobody re-reads.
/// </summary>
public sealed record EntitlementResponse(IReadOnlyList<string> Scopes);
