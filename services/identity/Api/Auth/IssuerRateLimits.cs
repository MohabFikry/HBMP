using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// Phase 18.B3 (audit R2 S9) — per-route rate limits on the issuer's credential endpoints.
///
/// Kong applies one global 1200/min per node. For an API that is a sane ceiling; for the four endpoints where
/// a secret is GUESSED it is no limit at all. ASP.NET Identity's lockout stops per-account password guessing,
/// but three things slip past it entirely:
///   • password SPRAYING — one common password against a thousand usernames trips no account's lockout;
///   • TOTP guessing on <c>/connect/2fa</c> — a 6-digit code is 1,000,000 values, and the two-factor path does
///     not increment the password lockout counter, so at 1200/min a code is brute-forcible inside its window;
///   • <c>/connect/token</c> client-secret guessing, which has no lockout concept at all.
///
/// The partition is the client IP, so one abusive source cannot deny service to the rest of the tenant. That
/// is imperfect behind a shared NAT — a clinic on one egress IP shares a budget — which is why the limits are
/// per-minute and generous relative to human use (a person types one password and one code) rather than tight.
/// A rejected request gets 429 with Retry-After, never a hint about whether the credential was close.
/// </summary>
public static class IssuerRateLimits
{
    public const string Credential = "issuer-credential";
    public const string Token = "issuer-token";

    public static IServiceCollection AddIssuerRateLimits(this IServiceCollection services) =>
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (ctx, _) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };

            // Interactive credential entry: sign-in, TOTP challenge, enrolment. A human needs a handful of
            // attempts after a typo; ten a minute leaves room for that and none for a search.
            o.AddPolicy(Credential, http => RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(http),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,   // shed, never queue — a queued auth attempt is just a slower guess
                }));

            // The token endpoint is machine traffic: code exchange and refresh. Higher, because a busy SPA
            // legitimately refreshes on a 5-minute access-token lifetime and several tabs may renew at once.
            o.AddPolicy(Token, http => RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(http),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

    /// <summary>Partition key: the client IP. Falls back to a single shared bucket when the address is absent
    /// (rather than to no limit) — an unattributable caller is exactly the one to keep on a short leash.</summary>
    private static string ClientKey(HttpContext http) =>
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown-client";
}
