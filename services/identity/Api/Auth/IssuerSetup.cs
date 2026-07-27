using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// Wires the OpenIddict authorization server (17.2) on top of the identity store: the Identity sign-in stack
/// (application/two-factor/external cookies + SignInManager + token providers) and the OpenIddict server
/// configured to emit the FROZEN token contract — RS256 signed, unencrypted access tokens (services validate
/// via JWKS in libs/auth), aud=hbmp-api, 300s lifetime, auth-code+PKCE / client-credentials / refresh.
/// See docs/adr/0015-in-app-identity-openiddict.md + docs/security/token-contract.md.
/// </summary>
public static class IssuerSetup
{
    public static IServiceCollection AddMersalIssuer(
        this IServiceCollection services, IConfiguration config, IWebHostEnvironment env)
    {
        // Sign-in manager + token providers (framework layer). AddIdentityCore already ran in
        // AddIdentityInfrastructure; re-acquire the builder to add the sign-in + TOTP/recovery providers.
        new IdentityBuilder(typeof(ApplicationUser), typeof(ApplicationRole), services)
            .AddSignInManager()
            .AddDefaultTokenProviders();

        // The four Identity cookies SignInManager expects (application is the primary; the two-factor +
        // external cookies are used by the 17.3 login flow).
        // 18.B3 (audit R2 S4) — cookie hardening. These four cookies ARE the issuer session: the application
        // cookie carries the amr claims that satisfy MFA, and TwoFactorUserId identifies the half-authenticated
        // user between the password step and the TOTP step. They shipped on framework defaults —
        // SecurePolicy=SameAsRequest and SameSite=Lax — so a single plaintext request leaked the session cookie
        // on the wire, and Lax still rides along on a top-level cross-site GET.
        //
        // Development is the one exception, and only for Secure: Tier 1 runs over http on localhost, and
        // Always there would make it impossible to log in at all. SameSite=Strict applies everywhere — nothing
        // in this flow is a legitimate cross-site navigation, because the SPA uses the authorization-code
        // redirect rather than a cross-site form post.
        void Harden(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions o)
        {
            o.Cookie.SecurePolicy = env.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.HttpOnly = true;   // explicit: no script on any origin reads the session
        }

        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, o =>
            {
                o.LoginPath = "/connect/login";
                o.Cookie.Name = "mersal.idp";
                o.ExpireTimeSpan = TimeSpan.FromMinutes(30); // aligns with the SSO idle window
                o.SlidingExpiration = true;
                Harden(o);
            })
            .AddCookie(IdentityConstants.ExternalScheme, Harden)
            .AddCookie(IdentityConstants.TwoFactorUserIdScheme, Harden)
            .AddCookie(IdentityConstants.TwoFactorRememberMeScheme, Harden);

        // 18.B3 (S4) — the antiforgery cookie travels with the same rules as the session it protects.
        services.AddAntiforgery(o =>
        {
            o.Cookie.Name = "mersal.idp.csrf";
            o.Cookie.SecurePolicy = env.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.FormFieldName = "__hbmp_csrf";
        });

        services.AddScoped<TokenPrincipalFactory>();

        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<IdentityStoreDbContext>())
            .AddServer(o =>
            {
                o.SetAuthorizationEndpointUris("connect/authorize")
                 .SetTokenEndpointUris("connect/token")
                 .SetUserinfoEndpointUris("connect/userinfo")
                 .SetLogoutEndpointUris("connect/logout");

                o.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange()
                 .AllowClientCredentialsFlow()
                 .AllowRefreshTokenFlow();

                // The frozen scope vocabulary (docs/security/token-contract.md §2) + OIDC standard scopes.
                o.RegisterScopes([.. IdentityContract.Scopes, "openid", "profile", "email", "offline_access"]);

                o.SetAccessTokenLifetime(TimeSpan.FromMinutes(5))   // frozen: 300s
                 .SetRefreshTokenLifetime(TimeSpan.FromHours(10));  // frozen: SSO max 36000s

                // RS256 signing; DO NOT encrypt access tokens — services validate a plain signed JWT via JWKS.
                // Dev/test use ephemeral keys; production uses persistent RS256 keys from OpenBao (fail-fast
                // if unconfigured — no dev-cert fallback). See IssuerKeys + docs/adr/0015 (phase-12 update).
                IssuerKeys.Configure(o, config, env.IsDevelopment());
                o.DisableAccessTokenEncryption();

                var aspnet = o.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableUserinfoEndpointPassthrough()
                    .EnableLogoutEndpointPassthrough();

                // Local Tier-1 dev runs over http (Kong terminates TLS in the deployed tiers).
                if (env.IsDevelopment()) aspnet.DisableTransportSecurityRequirement();
            })
            .AddValidation(o =>
            {
                o.UseLocalServer();
                o.UseAspNetCore();
            });

        services.AddHostedService<ClientSeeder>();
        services.AddHostedService<UserSeeder>(); // demo staff accounts (dev-only; 17.6 cutover)
        return services;
    }
}
