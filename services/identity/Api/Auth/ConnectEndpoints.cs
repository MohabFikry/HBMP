using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.AspNetCore; // OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest(HttpContext)
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
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
            HttpContext http, UserManager<ApplicationUser> users, UserClaimsService claims, TokenPrincipalFactory factory) =>
        {
            var request = http.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

            var auth = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
            if (!auth.Succeeded)
            {
                // Not signed in → bounce to the (minimal) login, returning here afterwards.
                return Results.Challenge(
                    new AuthenticationProperties { RedirectUri = http.Request.PathBase + http.Request.Path + http.Request.QueryString },
                    [IdentityConstants.ApplicationScheme]);
            }

            var user = await users.GetUserAsync(auth.Principal!);
            if (user is null || !user.IsActive)
                return Results.Forbid(properties: null, [IdentityConstants.ApplicationScheme]);

            var facts = await claims.ForAsync(user, http.RequestAborted);
            // The factors performed were stamped on the application cookie at sign-in (pwd, and otp when 2FA
            // was completed); carry them onto the token so MfaEvaluator can gate protected scopes.
            var amr = auth.Principal!.FindAll(AccountPages.AmrClaim).Select(c => c.Value).ToArray();
            var principal = factory.ForUser(facts, request.GetScopes(), amr.Length > 0 ? amr : ["pwd"]);
            return Results.SignIn(principal, properties: null, Scheme);
        });

        // ---- Token endpoint (authorization_code / refresh_token / client_credentials) -----------------------
        app.MapPost("/connect/token", async (
            HttpContext http, UserManager<ApplicationUser> users, UserClaimsService claims, TokenPrincipalFactory factory) =>
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
                var facts = await claims.ForAsync(user, http.RequestAborted);
                var amr = stored.Principal.GetClaims(TokenPrincipalFactory.AmrClaim);
                var principal = factory.ForUser(facts, stored.Principal.GetScopes(),
                    amr.Length > 0 ? amr : ["pwd"]);
                return Results.SignIn(principal, properties: null, Scheme);
            }

            return Forbid(Errors.UnsupportedGrantType, "The specified grant type is not supported.");
        });

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

        // ---- Password sign-in (17.3 login UI); routes to TOTP when the account has 2FA enabled -------------
        app.MapGet("/connect/login", (HttpContext http, string? returnUrl) =>
            Results.Content(AccountPages.LoginPage(LangOf(http), returnUrl), "text/html"));

        app.MapPost("/connect/login", async (
            HttpContext http, [FromForm] string username, [FromForm] string password, [FromForm] string? returnUrl,
            SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users) =>
        {
            var lang = LangOf(http);
            var user = await users.FindByNameAsync(username);
            if (user is null || !user.IsActive)
                return Results.Content(AccountPages.LoginPage(lang, returnUrl, error: true), "text/html");

            var result = await signIn.PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true);
            if (result.RequiresTwoFactor)
            {
                var q = string.IsNullOrEmpty(returnUrl) ? "" : $"?returnUrl={Uri.EscapeDataString(AccountPages.SafeReturn(returnUrl))}";
                return Results.Redirect($"/connect/2fa{q}");
            }
            if (!result.Succeeded)
                return Results.Content(AccountPages.LoginPage(lang, returnUrl, error: true), "text/html");

            // Single-factor success: stamp amr=pwd. Protected scopes will still be denied downstream until the
            // user enrols a second factor (/connect/enroll-2fa) — that is the MFA gate (C3) on the new issuer.
            await AccountPages.StampSignIn(http, signIn, user, ["pwd"]);
            return Results.Redirect(AccountPages.SafeReturn(returnUrl));
        }).DisableAntiforgery();

        app.MapPost("/connect/logout", async (HttpContext http, SignInManager<ApplicationUser> signIn) =>
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
}
