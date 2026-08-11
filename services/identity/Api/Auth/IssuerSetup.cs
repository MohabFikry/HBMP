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
    /// <summary>The named token provider the password-reset link is signed with (28.6).</summary>
    public const string PasswordResetTokenProvider = "MersalPasswordReset";

    public static IServiceCollection AddMersalIssuer(
        this IServiceCollection services, IConfiguration config, IWebHostEnvironment env)
    {
        // Sign-in manager + token providers (framework layer). AddIdentityCore already ran in
        // AddIdentityInfrastructure; re-acquire the builder to add the sign-in + TOTP/recovery providers.
        new IdentityBuilder(typeof(ApplicationUser), typeof(ApplicationRole), services)
            .AddSignInManager()
            .AddDefaultTokenProviders()
            // 28.6 — a password-reset token with its OWN lifespan (ADR-0036 §6.1).
            //
            // A NAMED provider, not a lowering of DataProtectionTokenProviderOptions.TokenLifespan. That
            // setting is GLOBAL: shortening the reset window to 30 minutes through it would silently shorten
            // email confirmation, change-email and every other data-protection token to 30 minutes as a side
            // effect — a change nobody made, in features nobody was looking at.
            .AddTokenProvider<DataProtectorTokenProvider<ApplicationUser>>(PasswordResetTokenProvider);

        // 30 minutes. Long enough to walk to a phone and read an inbox, short enough that a link forwarded,
        // logged by a mail gateway, or left in a browser history is dead by the time anybody finds it.
        services.Configure<DataProtectionTokenProviderOptions>(
            PasswordResetTokenProvider, o => o.TokenLifespan = TimeSpan.FromMinutes(30));

        // Point the RESET flow at it. Without this line the named provider exists and nothing uses it —
        // `GeneratePasswordResetTokenAsync` would keep issuing a default-lifespan token and the 30 minutes
        // above would be a number in a config file with no effect anywhere.
        services.Configure<IdentityOptions>(o => o.Tokens.PasswordResetTokenProvider = PasswordResetTokenProvider);

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
            // 28.3 — a HEADER name as well as a form field. The server-rendered pages post a form and use the
            // field; the SPA posts JSON and has no form to put a hidden input in. Without this the API could
            // not present a token at all, and the only ways out are both bad: drop antiforgery on the JSON
            // endpoints (the enrolment CSRF in AccountPages is account takeover, not an inconvenience), or
            // make the SPA send form-encoded bodies to keep a defence working by accident.
            o.HeaderName = "X-HBMP-CSRF";
        });

        services.AddScoped<TokenPrincipalFactory>();

        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<IdentityStoreDbContext>())
            .AddServer(o =>
            {
                // PIN THE ISSUER. Without this OpenIddict derives its issuer from the REQUEST — so a token
                // minted at http://localhost:8090/ (the browser's URL) was rejected by identity-service's own
                // local validation when the same request arrived through Kong, whose Host is localhost:8000:
                // "The issuer associated to the specified token is not valid" (ID2088). Every /identity/admin
                // surface 401ed through the gateway while working perfectly on the direct port, which is the
                // worst shape a bug can take — it looks like a permissions problem and it is a URL problem.
                // The 19 JWKS-validating services already pin theirs via Auth__ValidIssuers; this is the
                // issuer's own half of the same fix.
                o.SetIssuer(new Uri(config["Issuer:PublicUrl"] ?? "http://localhost:8090/"));

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

        // 28.11 — OpenIddict persists a row per artefact and prunes none of them by default. Its own pruning
        // ships as a Quartz job; this is the same work as a plain hosted service, which keeps a scheduler and
        // its tables out of a service that has no other scheduled work. See TokenPruner.
        services.AddHostedService<TokenPruner>();
        services.AddHostedService<ClientSeeder>();
        services.AddHostedService<UserSeeder>(); // demo staff accounts (dev-only; 17.6 cutover)
        // Registered unconditionally, not behind the demo-seeding flag: a real deployment can provision a
        // pharmacist with no provider just as easily as the seeder did, and the symptom — a login that works
        // until it reaches its own portal — is equally opaque there.
        services.AddHostedService<ProviderBindingCheck>();
        return services;
    }
}
