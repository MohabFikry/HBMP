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
///
/// <para>
/// <b>28.1 — the partition was the GATEWAY's address, not the client's.</b> Every sentence above was true of
/// the intent and false of the behaviour: behind Kong, <c>RemoteIpAddress</c> is Kong, so "one abusive source
/// cannot deny service to the rest of the tenant" was exactly inverted — one source could, because there was
/// one bucket. Ten requests a minute, unauthenticated, and nobody on the platform can sign in. See
/// <see cref="ClientPartition"/> for the proof and the two independent routes by which it happened, and
/// <see cref="ClientAddressResolver"/> for the trust decision.
/// </para>
/// </summary>
public static class IssuerRateLimits
{
    public const string Credential = "issuer-credential";
    public const string Token = "issuer-token";

    /// <summary>The shipped limits. A human types one password and one code; ten a minute leaves room for
    /// typos and none for a search.</summary>
    public const int DefaultCredentialPerMinute = 10;

    /// <summary>Machine traffic: code exchange and refresh. Higher, because a busy SPA legitimately refreshes
    /// on a 5-minute access-token lifetime and several tabs may renew at once.</summary>
    public const int DefaultTokenPerMinute = 60;

    /// <param name="config">
    /// Optional. Supplies <c>RateLimits:CredentialPerMinute</c> / <c>RateLimits:TokenPerMinute</c>.
    ///
    /// <para>
    /// Configurable because of the TEST host, and that is worth being straight about rather than dressing up.
    /// Every request in an in-process test suite arrives from one address, so the suite shares one partition
    /// and throttles ITSELF — the 28.3 session tests turned red at the eleventh sign-in, which is the limiter
    /// working exactly as designed and telling us nothing. The alternative, sleeping a minute between tests,
    /// buys nothing and costs the suite.
    /// </para>
    /// <para>
    /// The DEFAULT is unchanged and pinned by a test, so a deployment that configures nothing gets the
    /// shipped numbers. Making a security limit configurable is only safe while the default is the safe one.
    /// </para>
    /// </param>
    public static IServiceCollection AddIssuerRateLimits(
        this IServiceCollection services, IConfiguration? config = null)
    {
        services.AddSingleton<ClientAddressResolver>();

        // NOT read here. `config` is the WebApplicationBuilder's configuration as it stands during service
        // REGISTRATION, and sources added later — which is exactly what WebApplicationFactory does to
        // override settings for a test host — are not in it yet. Reading eagerly produced a limiter that
        // ignored its own configuration and reported 429 while the value said 10000: a setting that appears
        // to exist and does nothing. Resolved per request instead, from a singleton built after the host is.
        services.AddSingleton(sp => new IssuerRateLimitBudget(sp.GetService<IConfiguration>() ?? config));

        return services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (ctx, _) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };

            // Interactive credential entry: sign-in, TOTP challenge, enrolment.
            o.AddPolicy(Credential, http => RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(http),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = http.RequestServices.GetRequiredService<IssuerRateLimitBudget>().Credential,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,   // shed, never queue — a queued auth attempt is just a slower guess
                }));

            // The token endpoint is machine traffic.
            o.AddPolicy(Token, http => RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(http),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = http.RequestServices.GetRequiredService<IssuerRateLimitBudget>().Token,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });
    }

    private static string ClientKey(HttpContext http)
    {
        var resolver = http.RequestServices.GetRequiredService<ClientAddressResolver>();
        var key = resolver.PartitionKey(http, out var misconfigured);

        if (misconfigured)
        {
            // Loud on purpose. This is the exact state the platform was in before 28.1 — every caller sharing
            // one bucket — and the only thing that distinguishes it from ordinary traffic is this line.
            http.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(IssuerRateLimits))
                .LogWarning(
                    "Credential request arrived via a trusted proxy ({Peer}) with no usable X-Forwarded-For "
                    + "client address at {Hops} hop(s). Rate limiting has fallen back to a SHARED bucket for "
                    + "every caller — check Forwarding:TrustedHops against the actual proxy chain.",
                    http.Connection.RemoteIpAddress, resolver.TrustedHops);
        }
        return key;
    }
}

/// <summary>
/// The permit counts, resolved once the host's configuration is complete.
///
/// <para>
/// A separate singleton rather than two captured ints, because the capture happened too early to see the
/// configuration it was meant to read. Anything derived from <c>IConfiguration</c> inside a service
/// registration has this hazard; making it a service moves the read to a point where the answer is final.
/// </para>
/// </summary>
public sealed class IssuerRateLimitBudget
{
    public IssuerRateLimitBudget(IConfiguration? config)
    {
        Credential = Positive(config?["RateLimits:CredentialPerMinute"], IssuerRateLimits.DefaultCredentialPerMinute);
        Token = Positive(config?["RateLimits:TokenPerMinute"], IssuerRateLimits.DefaultTokenPerMinute);
    }

    public int Credential { get; }
    public int Token { get; }

    /// <summary>A configured limit, or the shipped default. Zero, negative and unparseable all fall back to
    /// the default rather than to "no limit" — a typo in a config file must not silently remove a control.</summary>
    private static int Positive(string? configured, int fallback) =>
        int.TryParse(configured, out var n) && n > 0 ? n : fallback;
}
