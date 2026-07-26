using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Mersal.Auth;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Mersal.Identity.Tests;

/// <summary>The identity-service under test (Development env, pointed at IDENTITY_TEST_DB).</summary>
public sealed class IdentityAppFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = IdentityTestDb.Conn,
        }));
        return base.CreateHost(builder);
    }
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
        foreach (var r in roles) (await users.AddToRoleAsync(user, r)).Succeeded.Should().BeTrue();
        string? key = null;
        if (twoFactor)
        {
            await users.ResetAuthenticatorKeyAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
            await users.SetTwoFactorEnabledAsync(user, true);
        }
        return (user.Id, key);
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

    /// <summary>Full auth-code + PKCE, optionally completing TOTP when <paramref name="totpKey"/> is supplied.</summary>
    public static async Task<string> AuthCodeToken(
        IdentityAppFactory factory, string username, string password, string? totpKey, string scope)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        const string redirect = "http://localhost:5173/";
        var authorizeUrl = "/connect/authorize?response_type=code"
            + $"&client_id={IdentityContract.WebClientId}&redirect_uri={Uri.EscapeDataString(redirect)}"
            + $"&scope={Uri.EscapeDataString(scope)}&code_challenge={challenge}&code_challenge_method=S256&state=xyz";

        var login = await client.PostAsync("/connect/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username, ["password"] = password, ["returnUrl"] = authorizeUrl,
        }));

        if (login.Headers.Location?.ToString().StartsWith("/connect/2fa", StringComparison.Ordinal) == true)
        {
            totpKey.Should().NotBeNull("the account has 2FA enabled but no TOTP key was supplied");
            var twofa = await client.PostAsync("/connect/2fa", new FormUrlEncodedContent(new Dictionary<string, string>
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
        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var code = Uri.UnescapeDataString(new Uri(authorize.Headers.Location!.ToString()).Query
            .TrimStart('?').Split('&').Select(p => p.Split('=', 2)).First(p => p[0] == "code")[1]);

        return await PostToken(client, new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = IdentityContract.WebClientId,
            ["redirect_uri"] = redirect, ["code"] = code, ["code_verifier"] = verifier,
        });
    }

    public static async Task<string> PostToken(HttpClient client, Dictionary<string, string> form)
    {
        var resp = await client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        var body = await resp.Content.ReadAsStringAsync();
        resp.IsSuccessStatusCode.Should().BeTrue($"token request should succeed, got {(int)resp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString()!;
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
}
