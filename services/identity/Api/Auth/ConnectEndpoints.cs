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
            // 17.3 adds "otp" when a second factor was performed; the minimal login is password-only.
            var principal = factory.ForUser(facts, request.GetScopes(), ["pwd"]);
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

        // ---- Minimal password sign-in (REPLACED by the 17.3 login UI + 2FA) -------------------------------
        app.MapGet("/connect/login", (string? returnUrl) => Results.Content(LoginForm(returnUrl), "text/html"));

        app.MapPost("/connect/login", async (
            HttpContext http, [FromForm] string username, [FromForm] string password, [FromForm] string? returnUrl,
            SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users) =>
        {
            var user = await users.FindByNameAsync(username);
            if (user is null || !user.IsActive)
                return Results.Content(LoginForm(returnUrl, "Invalid credentials."), "text/html");

            var result = await signIn.PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true);
            // NOTE: 17.3 handles result.RequiresTwoFactor (TOTP) + recovery + step-up. Here, success = signed in.
            if (!result.Succeeded)
                return Results.Content(LoginForm(returnUrl, result.IsLockedOut ? "Account locked." : "Invalid credentials."), "text/html");

            return Results.Redirect(SafeReturn(returnUrl));
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

    // A relative, same-app return path only (no open redirect).
    private static string SafeReturn(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            ? returnUrl : "/";

    private static string LoginForm(string? returnUrl, string? error = null)
    {
        var ret = System.Net.WebUtility.HtmlEncode(SafeReturn(returnUrl));
        var err = error is null ? "" : $"<p role=\"alert\" style=\"color:#b91c1c\">{System.Net.WebUtility.HtmlEncode(error)}</p>";
        // Minimal, unstyled bootstrap form — the accessible bilingual login UI lands in 17.3.
        return $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Mersal — Sign in</title></head>
        <body><h1>Mersal — Sign in</h1>{{err}}
        <form method="post" action="/connect/login">
          <input type="hidden" name="returnUrl" value="{{ret}}" />
          <p><label>Username <input name="username" autocomplete="username" required></label></p>
          <p><label>Password <input name="password" type="password" autocomplete="current-password" required></label></p>
          <button type="submit">Sign in</button>
        </form></body></html>
        """;
    }
}
