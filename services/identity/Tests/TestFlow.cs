using System.Text.RegularExpressions;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Mersal.Auth;
using Mersal.Email;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Mersal.Identity.Tests;

/// <summary>The identity-service under test (Development env, pointed at IDENTITY_TEST_DB).</summary>
public sealed class IdentityAppFactory : WebApplicationFactory<Program>
{
    /// <summary>The m2m secret this test host seeds and the tests authenticate with. A test-only value, and
    /// deliberately not a plausible one — the gitleaks rules added in 18.B1 scan for credential-shaped
    /// literals, and a realistic-looking secret here would be indistinguishable from a real leak.</summary>
    public const string ServiceSecret = "test-harness-m2m-secret";

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = IdentityTestDb.Conn,
            ["Issuer:SeedDemoUsers"] = "false", // don't seed demo staff into the shared test DB
            // 18.E1 — the harness supplies the m2m secret EXPLICITLY.
            //
            // Before 18.B1 the seeder fell back to a literal (`dev-service-secret-change-me`) and these
            // tests hardcoded the same string. B1 removed that fallback — outside Development it is now a
            // startup failure, and in Development it is a RANDOM per-run value, so a known secret can never
            // be minted. The tests kept the literal and started failing with invalid_client, and nobody saw
            // it because IDENTITY_TEST_DB was never exported in CI (Q2): they skipped on every run.
            ["Issuer:ServiceClientSecret"] = ServiceSecret,
            // 28.3 — every request in this in-process host arrives from ONE address, so the whole suite
            // shares one credential-rate-limit partition and throttles itself: the session tests turned red
            // at the eleventh sign-in with a 429, which is the limiter working exactly as designed and
            // telling us nothing about the code under test. Raised HERE, in the harness, so the shipped
            // default stays 10 — and `TheDefaultCredentialLimitIsTen` fails if anyone changes that.
            ["RateLimits:CredentialPerMinute"] = "10000",
            ["RateLimits:TokenPerMinute"] = "10000",
        }));
        // 28.6 — a capturing email transport. The reset flow REFUSES to run without one (it answers 503
        // rather than reporting a send it cannot make), so a test host with no transport could only ever
        // exercise the refusal. This one reports IsConfigured and keeps what was "sent", so a test can read
        // the link a person would have clicked — which is how the base64url round trip gets exercised at all.
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(CapturedEmail.Instance);
        });
        return base.CreateHost(builder);
    }
}

/// <summary>
/// The test host's email transport: reports itself configured and keeps every message instead of sending.
///
/// <para>A singleton because WebApplicationFactory builds its own container and the assertion happens outside
/// it. Cleared per test by the test that reads it — a shared sink that nobody clears reads as "the last run's
/// mail", which is worse than no evidence.</para>
/// </summary>
public sealed class CapturedEmail : IEmailSender
{
    public static readonly CapturedEmail Instance = new();
    private readonly List<(string To, string Subject, string Text)> _sent = [];

    public bool IsConfigured => true;

    public Task SendAsync(string toAddress, string subject, string htmlBody, string textBody, CancellationToken ct = default)
    {
        lock (_sent) _sent.Add((toAddress, subject, textBody));
        return Task.CompletedTask;
    }

    public IReadOnlyList<(string To, string Subject, string Text)> Sent
    {
        get { lock (_sent) return _sent.ToList(); }
    }

    public void Clear() { lock (_sent) _sent.Clear(); }
}

/// <summary>Drives the real OIDC flows against the test server so multiple test classes share one path:
/// client-credentials, and the SPA auth-code+PKCE flow (optionally through the TOTP second factor), plus
/// JWKS-discovery token validation into an <see cref="HbmpPrincipal"/> — exactly as a service would.</summary>
public static class TestFlow
{
    public const string TenantA = "11111111-1111-1111-1111-111111111111";

    /// <summary>Seed a user (optionally 2FA-enabled) directly through the store; returns its id + TOTP key.</summary>
    public static async Task<(Guid Id, string? TotpKey)> SeedUser(
        IdentityAppFactory factory, string username, string password, IEnumerable<string> roles,
        bool twoFactor = false, string tenant = TenantA, Guid? providerId = null)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = username, Email = $"{username}@example.org",
            TenantId = tenant, ProviderId = providerId, DisplayName = username,
            CreatedAt = DateTimeOffset.UtcNow, EmailConfirmed = true,
        };
        (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
        var roleList = roles.ToList();
        foreach (var r in roleList) (await users.AddToRoleAsync(user, r)).Succeeded.Should().BeTrue();

        // 21.1c — a seeded user needs the MEMBERSHIP it is minted from. 0010 backfills only what existed when
        // it ran, so a user created here would otherwise sign in and then be refused at authorize.
        await scope.ServiceProvider.GetRequiredService<MembershipService>()
            .EnsureMirroredAsync(user, roleList, "test:seed");

        string? key = null;
        if (twoFactor)
        {
            await users.ResetAuthenticatorKeyAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
            await users.SetTwoFactorEnabledAsync(user, true);
        }
        return (user.Id, key);
    }

    /// <summary>Give an existing identity a membership in ANOTHER tenant — the multi-membership shape the
    /// admin surface will create in 21.5, and the one 21.1c's chooser exists for. Written directly through the
    /// store because <c>EnsureMirroredAsync</c> deliberately only ever touches the identity's own tenant.</summary>
    public static async Task<Guid> SeedMembership(
        IdentityAppFactory factory, Guid userId, string tenant, IEnumerable<string> roles,
        MembershipStatus status = MembershipStatus.Active, Guid? providerId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
        var now = DateTimeOffset.UtcNow;
        var m = new TenantMembership
        {
            MembershipId = Guid.NewGuid(), UserId = userId, TenantId = tenant, ProviderId = providerId,
            Status = status, ActivatedAt = now, CreatedBy = "test:seed", CreatedAt = now,
            UpdatedBy = "test:seed", UpdatedAt = now,
        };
        db.Memberships.Add(m);
        foreach (var r in roles)
        {
            var normalized = r.ToUpperInvariant();
            var role = await db.Roles.FirstAsync(x => x.NormalizedName == normalized);
            db.MembershipRoles.Add(new MembershipRole
            {
                MembershipId = m.MembershipId, RoleId = role.Id, GrantedBy = "test:seed", GrantedAt = now,
            });
        }
        await db.SaveChangesAsync();
        return m.MembershipId;
    }

    /// <summary>Re-run the expand-phase mirror for an identity, as the admin role-setting endpoint does.</summary>
    public static async Task MirrorRoles(IdentityAppFactory factory, Guid userId, IEnumerable<string> roles)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
        var user = await db.Users.FirstAsync(u => u.Id == userId);
        await scope.ServiceProvider.GetRequiredService<MembershipService>()
            .EnsureMirroredAsync(user, roles, "test:mirror");
    }

    /// <summary>The membership id an identity holds in a given tenant (the one SeedUser mirrored).</summary>
    public static async Task<Guid> MembershipIdOf(IdentityAppFactory factory, Guid userId, string tenant)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
        return (await db.Memberships.AsNoTracking()
            .FirstAsync(m => m.UserId == userId && m.TenantId == tenant && !m.IsDeleted)).MembershipId;
    }

    /// <summary>Move a membership's lifecycle state — used to prove that revocation bites mid-session.</summary>
    public static async Task SetMembershipStatus(IdentityAppFactory factory, Guid membershipId, MembershipStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
        var m = await db.Memberships.FirstAsync(x => x.MembershipId == membershipId);
        m.Status = status;
        m.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    public static async Task DeleteUser(IdentityAppFactory factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
        var u = await db.Users.FindAsync(id);
        if (u is not null) { db.Users.Remove(u); await db.SaveChangesAsync(); }
    }

    public static async Task<string> ClientCredentialsToken(HttpClient client, string clientId, string secret, string scope) =>
        await PostToken(client, new()
        {
            ["grant_type"] = "client_credentials", ["client_id"] = clientId,
            ["client_secret"] = secret, ["scope"] = scope,
        });

    /// <summary>Full auth-code + PKCE, optionally completing TOTP when <paramref name="totpKey"/> is supplied.
    /// <paramref name="membership"/> answers the 21.1c chooser; it is only needed when the identity holds more
    /// than one selectable membership, since a single one auto-selects.</summary>
    public static async Task<string> AuthCodeToken(
        IdentityAppFactory factory, string username, string password, string? totpKey, string scope,
        Guid? membership = null) =>
        (await AuthCodeTokens(factory, username, password, totpKey, scope, membership)).Access;

    /// <inheritdoc cref="AuthCodeToken"/>
    public static async Task<(string Access, string? Refresh)> AuthCodeTokens(
        IdentityAppFactory factory, string username, string password, string? totpKey, string scope,
        Guid? membership = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        const string redirect = "http://localhost:5173/";
        var authorizeUrl = "/connect/authorize?response_type=code"
            + $"&client_id={IdentityContract.WebClientId}&redirect_uri={Uri.EscapeDataString(redirect)}"
            + $"&scope={Uri.EscapeDataString(scope)}&code_challenge={challenge}&code_challenge_method=S256&state=xyz";

        var loginForm = await AntiforgeryFields(client, "/connect/login");
        var login = await client.PostAsync("/connect/login", new FormUrlEncodedContent(new Dictionary<string, string>(loginForm)
        {
            ["username"] = username, ["password"] = password, ["returnUrl"] = authorizeUrl,
        }));

        if (login.Headers.Location?.ToString().StartsWith("/connect/2fa", StringComparison.Ordinal) == true)
        {
            totpKey.Should().NotBeNull("the account has 2FA enabled but no TOTP key was supplied");
            var twofaForm = await AntiforgeryFields(client, "/connect/2fa");
            var twofa = await client.PostAsync("/connect/2fa", new FormUrlEncodedContent(new Dictionary<string, string>(twofaForm)
            {
                ["code"] = Totp(totpKey!), ["returnUrl"] = authorizeUrl,
            }));
            twofa.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }
        else
        {
            login.StatusCode.Should().Be(HttpStatusCode.Redirect, "password sign-in should redirect");
        }

        var authorize = await client.GetAsync(authorizeUrl);

        // 21.1c — several selectable memberships ⇒ authorize cannot decide which organization this session
        // acts for and sends the browser to the chooser. Drive it exactly as a person would: load the form,
        // post the selection, then come back to authorize, which now resolves.
        if (authorize.Headers.Location?.ToString().StartsWith("/connect/select-membership", StringComparison.Ordinal) == true)
        {
            membership.Should().NotBeNull("this identity holds several memberships, so the test must say which one to act under");
            var chooser = await AntiforgeryFields(client, authorize.Headers.Location!.ToString());
            var chosen = await client.PostAsync("/connect/select-membership", new FormUrlEncodedContent(
                new Dictionary<string, string>(chooser)
                {
                    ["membershipId"] = membership!.Value.ToString(), ["returnUrl"] = authorizeUrl,
                }));
            chosen.StatusCode.Should().Be(HttpStatusCode.Redirect, "a valid selection returns to the authorize request");
            authorize = await client.GetAsync(authorizeUrl);
        }

        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var code = Uri.UnescapeDataString(new Uri(authorize.Headers.Location!.ToString()).Query
            .TrimStart('?').Split('&').Select(p => p.Split('=', 2)).First(p => p[0] == "code")[1]);

        return await PostTokens(client, new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = IdentityContract.WebClientId,
            ["redirect_uri"] = redirect, ["code"] = code, ["code_verifier"] = verifier,
        });
    }

    /// <summary>
    /// Sign in, then GET /connect/authorize once, handing back the RAW response.
    ///
    /// 21.1c's interesting outcomes are not tokens: a refusal when no membership is selectable, and a redirect
    /// to the chooser when several are. Both are invisible to <see cref="AuthCodeTokens"/>, which asserts its
    /// way to a code. The client is returned still holding its cookies so the chooser can then be driven.
    /// </summary>
    public static async Task<(HttpClient Client, HttpResponseMessage Authorize, string AuthorizeUrl)> LoginThenAuthorize(
        IdentityAppFactory factory, string username, string password, string scope)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(Base64Url(RandomNumberGenerator.GetBytes(32)))));
        var authorizeUrl = "/connect/authorize?response_type=code"
            + $"&client_id={IdentityContract.WebClientId}&redirect_uri={Uri.EscapeDataString("http://localhost:5173/")}"
            + $"&scope={Uri.EscapeDataString(scope)}&code_challenge={challenge}&code_challenge_method=S256&state=xyz";

        var loginForm = await AntiforgeryFields(client, "/connect/login");
        var login = await client.PostAsync("/connect/login", new FormUrlEncodedContent(new Dictionary<string, string>(loginForm)
        {
            ["username"] = username, ["password"] = password, ["returnUrl"] = authorizeUrl,
        }));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect, "password sign-in should succeed");

        return (client, await client.GetAsync(authorizeUrl), authorizeUrl);
    }

    /// <summary>POST a membership selection to the chooser, as the rendered form does.</summary>
    public static async Task<HttpResponseMessage> ChooseMembership(
        HttpClient client, string chooserUrl, Guid membershipId, string returnUrl)
    {
        var form = await AntiforgeryFields(client, chooserUrl);
        return await client.PostAsync("/connect/select-membership", new FormUrlEncodedContent(
            new Dictionary<string, string>(form)
            {
                ["membershipId"] = membershipId.ToString(), ["returnUrl"] = returnUrl,
            }));
    }

    public static async Task<string> PostToken(HttpClient client, Dictionary<string, string> form) =>
        (await PostTokens(client, form)).Access;

    /// <summary>The access token AND the refresh token, when <c>offline_access</c> was granted. 21.1c needs the
    /// refresh token to prove that ending a membership stops the session at the next exchange.</summary>
    public static async Task<(string Access, string? Refresh)> PostTokens(HttpClient client, Dictionary<string, string> form)
    {
        var resp = await client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        var body = await resp.Content.ReadAsStringAsync();
        resp.IsSuccessStatusCode.Should().BeTrue($"token request should succeed, got {(int)resp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return (doc.RootElement.GetProperty("access_token").GetString()!,
                doc.RootElement.TryGetProperty("refresh_token", out var r) ? r.GetString() : null);
    }

    /// <summary>A token request whose REFUSAL is the point — returns the status and body instead of asserting.</summary>
    public static async Task<(HttpStatusCode Status, string Body)> PostTokenRaw(
        HttpClient client, Dictionary<string, string> form)
    {
        var resp = await client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    /// <summary>Validate exactly as a service would: OIDC discovery → JWKS → RS256 + aud → HbmpPrincipal.</summary>
    public static async Task<HbmpPrincipal> Validate(HttpClient client, string token)
    {
        using var discovery = JsonDocument.Parse(
            await client.GetStringAsync("http://localhost/.well-known/openid-configuration"));
        var issuer = discovery.RootElement.GetProperty("issuer").GetString();
        var jwksUri = discovery.RootElement.GetProperty("jwks_uri").GetString();
        var keys = new JsonWebKeySet(await client.GetStringAsync(jwksUri)).GetSigningKeys();
        keys.Should().NotBeEmpty("the issuer must publish its RS256 signing key via JWKS");

        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = issuer, ValidAudience = IdentityContract.ApiResource, IssuerSigningKeys = keys,
            ValidateLifetime = true, NameClaimType = "sub", RoleClaimType = "roles",
        });
        result.IsValid.Should().BeTrue(result.Exception?.Message ?? "token should validate");
        return HbmpPrincipal.FromClaims(new ClaimsPrincipal(result.ClaimsIdentity));
    }

    public static string Totp(string base32Key)
    {
        var key = Base32Decode(base32Key);
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var msg = new byte[8];
        for (var i = 7; i >= 0; i--) { msg[i] = (byte)(counter & 0xff); counter >>= 8; }
#pragma warning disable CA5350 // RFC-6238 TOTP is defined over HMAC-SHA1; mirrors Identity's authenticator.
        var hash = HMACSHA1.HashData(key, msg);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0f;
        var bin = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (bin % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base32Decode(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        s = s.TrimEnd('=').ToUpperInvariant();
        var bits = 0; var value = 0; var output = new List<byte>();
        foreach (var c in s)
        {
            value = (value << 5) | alphabet.IndexOf(c); bits += 5;
            if (bits >= 8) { output.Add((byte)((value >> (bits - 8)) & 0xff)); bits -= 8; }
        }
        return output.ToArray();
    }

    /// <summary>
    /// 18.E1 — fetch a rendered form and return its antiforgery field, exactly as a browser does.
    ///
    /// 18.B3 (S4) removed `.DisableAntiforgery()` from the three credential POSTs, because a cross-site post
    /// to /connect/enroll-2fa registered the ATTACKER's authenticator as the victim's second factor. These
    /// tests posted credentials without ever loading the form — which no browser does — so they began
    /// failing the moment the protection landed. Nobody saw it: IDENTITY_TEST_DB was never exported in CI
    /// (Q2), so this whole suite skipped on every run.
    ///
    /// The GET also sets the antiforgery COOKIE on the shared HttpClient handler, which is the other half of
    /// the double-submit pair — so this must run against the same client that will post.
    /// </summary>
    /// <remarks>Made INTERNAL in 28.3 so the session-API parity tests can drive the form path with it. Those
    /// tests exist to compare the two sign-in paths against each other, which needs both drivers in one
    /// place; a second copy of this helper would be a second thing to keep true about a security control.</remarks>
    internal static async Task<Dictionary<string, string>> AntiforgeryFields(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var m = Regex.Match(html, @"<input type=""hidden"" name=""([^""]+)"" value=""([^""]*)"" />");
        m.Success.Should().BeTrue("the rendered form at {0} must carry an antiforgery field (18.B3 / S4)", path);
        return new Dictionary<string, string> { [m.Groups[1].Value] = m.Groups[2].Value };
    }
}
