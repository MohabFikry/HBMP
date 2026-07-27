using Mersal.Auth.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Mersal.Auth;

/// <summary>
/// One-call wiring for HBMP identity + access. Every service calls
/// <c>services.AddHbmpAuthentication(configuration)</c> and then
/// <c>app.UseAuthentication(); app.UseAuthorization();</c>.
///
/// Validates access tokens (issuer, audience, signature via JWKS, expiry) at the service
/// (defense in depth with the Kong gateway) and enforces MFA for scope-protected endpoints.
/// See phase-0-foundations.md (0.2) and CLAUDE.md § Security.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHbmpAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = new HbmpAuthOptions();
        configuration.GetSection(HbmpAuthOptions.SectionName).Bind(options);
        return services.AddHbmpAuthentication(options);
    }

    public static IServiceCollection AddHbmpAuthentication(
        this IServiceCollection services, HbmpAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Authority))
            throw new InvalidOperationException("Auth:Authority (the identity-service issuer URL) must be configured.");

        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        services.AddHttpContextAccessor();
        services.TryAddScoped<IHbmpPrincipalAccessor, HbmpPrincipalAccessor>();

        // Auth audit sink: no-op until libs/audit-client (0.3) registers the durable one.
        services.TryAddSingleton<IAuthEventSink>(NullAuthEventSink.Instance);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.Authority = options.Authority;
                jwt.Audience = options.Audience;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                jwt.MapInboundClaims = false; // keep the issuer's raw claim names (sub, scope, roles)

                // Accept the Authority issuer plus any explicitly-allowed issuers (split-horizon dev:
                // browser tokens carry iss=localhost:8090 while services fetch JWKS via identity-service:8080).
                // The JwtBearer handler additionally concatenates the discovered Authority issuer.
                //
                // Each is accepted BOTH with and without its trailing slash. An issuer identifier that differs
                // only by that slash is the same issuer to everyone except a string comparison, and OpenIddict
                // publishes `http://host/` while every configured value here was written `http://host` — so
                // every browser-issued token was rejected by every service, platform-wide, with
                // "The issuer 'http://localhost:8090/' is invalid": an error that names the issuer it just
                // refused and therefore reads as though the ISSUER is wrong rather than this list.
                //
                // This is not a loosening of issuer validation. `https://a.example/` and `https://a.example`
                // are the same origin and path; nothing else about the check changes, and an issuer that is
                // not in the list is still refused.
                var validIssuers = new List<string>();
                foreach (var issuer in new[] { options.Authority }.Concat(options.ValidIssuers))
                {
                    if (string.IsNullOrWhiteSpace(issuer)) continue;
                    var trimmed = issuer.TrimEnd('/');
                    validIssuers.Add(trimmed);
                    validIssuers.Add(trimmed + "/");
                }

                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuers = validIssuers,
                    ValidateAudience = !string.IsNullOrWhiteSpace(options.Audience),
                    ValidAudience = options.Audience,
                    ValidateIssuerSigningKey = true,   // signature via JWKS
                    ValidateLifetime = true,           // expiry
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ClockSkew = options.ClockSkew,
                    NameClaimType = HbmpClaimTypes.Subject,
                    RoleClaimType = "roles",
                };

                jwt.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = ctx =>
                    {
                        ctx.HttpContext.RequestServices.GetRequiredService<IAuthEventSink>()
                            .Record(new AuthEvent(AuthEventKind.TokenRejected, Subject: null,
                                Reason: ctx.Exception.GetType().Name));
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = ctx =>
                    {
                        var principal = HbmpPrincipal.FromClaims(ctx.Principal!);
                        ctx.HttpContext.RequestServices.GetRequiredService<IAuthEventSink>()
                            .Record(new AuthEvent(AuthEventKind.LoginSuccess, principal.Subject,
                                SessionId: principal.SessionId, SourceIp: principal.SourceIp));
                        return Task.CompletedTask;
                    },
                };
            });

        // Authorization: dynamic scope policies + MFA, default-deny handlers.
        services.AddAuthorization();
        services.AddSingleton<IAuthorizationPolicyProvider, ScopePolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, ScopeAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, MfaAuthorizationHandler>();

        return services;
    }
}
