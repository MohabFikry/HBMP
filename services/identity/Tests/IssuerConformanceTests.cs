using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Mersal.Auth;
using Mersal.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Mersal.Identity.Tests;

/// <summary>
/// 17.2 issuer conformance (docs/security/token-contract.md §6). Proves a token minted by the OpenIddict
/// issuer is validated by the SAME machinery libs/auth uses (JWKS discovery, RS256, aud=hbmp-api) and yields
/// the expected <see cref="HbmpPrincipal"/> — for both client-credentials and the SPA auth-code+PKCE flow.
/// Env-gated on IDENTITY_TEST_DB (a migrated Postgres). DB-less CI skips.
/// </summary>
[Collection("identity-db")]
public class IssuerConformanceTests
{
    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // App-config source (applied after appsettings) so the test DB wins over appsettings.Development.json.
            builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Identity"] = IdentityTestDb.Conn,
            }));
            return base.CreateHost(builder);
        }
    }

    [SkippableFact]
    public async Task Client_credentials_token_validates_through_libs_auth()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — issuer integration test skipped.");
        using var factory = new Factory();
        var client = factory.CreateClient();

        var token = await PostToken(client, new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = IdentityContract.ServiceClientId,
            ["client_secret"] = "dev-service-secret-change-me",
            ["scope"] = "finance:read notification:ingest",
        });

        var principal = await ValidateAsync(factory, client, token);
        principal.Subject.Should().Be(IdentityContract.ServiceClientId);
        principal.Scopes.Should().Contain("finance:read");
    }

    [SkippableFact]
    public async Task Authorization_code_pkce_token_carries_the_frozen_user_claims()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — issuer integration test skipped.");
        using var factory = new Factory();

        // Seed a finance user (role must exist from the migration seed).
        var userId = Guid.NewGuid();
        var uname = $"conf-{userId:N}";
        var tenant = "11111111-1111-1111-1111-111111111111";
        var providerId = Guid.NewGuid();
        const string password = "Passw0rd!Mersal";
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var u = new ApplicationUser
            {
                Id = userId, UserName = uname, Email = $"{uname}@example.org",
                TenantId = tenant, ProviderId = providerId, DisplayName = "Conf User",
                CreatedAt = DateTimeOffset.UtcNow, EmailConfirmed = true,
            };
            (await users.CreateAsync(u, password)).Succeeded.Should().BeTrue();
            (await users.AddToRoleAsync(u, "finance")).Succeeded.Should().BeTrue();
        }

        try
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            const string redirect = "http://localhost:5173/";
            var authorizeUrl = "/connect/authorize?response_type=code"
                + $"&client_id={IdentityContract.WebClientId}"
                + $"&redirect_uri={Uri.EscapeDataString(redirect)}"
                + $"&scope={Uri.EscapeDataString("openid finance:read audit:read offline_access")}"
                + $"&code_challenge={challenge}&code_challenge_method=S256&state=xyz";

            // Minimal password sign-in → auth cookie (17.3 replaces with the login UI + 2FA).
            var login = await client.PostAsync("/connect/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = uname, ["password"] = password, ["returnUrl"] = authorizeUrl,
            }));
            login.StatusCode.Should().Be(HttpStatusCode.Redirect);

            // Authorize → 302 back to redirect_uri with ?code=…
            var authorize = await client.GetAsync(authorizeUrl);
            authorize.StatusCode.Should().Be(HttpStatusCode.Redirect);
            var location = authorize.Headers.Location!.ToString();
            location.Should().StartWith(redirect);
            var pair = new Uri(location).Query.TrimStart('?').Split('&')
                .Select(p => p.Split('=', 2)).First(p => p[0] == "code");
            var code = Uri.UnescapeDataString(pair[1]);
            code.Should().NotBeNullOrEmpty();

            var token = await PostToken(client, new()
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = IdentityContract.WebClientId,
                ["redirect_uri"] = redirect,
                ["code"] = code!,
                ["code_verifier"] = verifier,
            });

            var principal = await ValidateAsync(factory, client, token);
            principal.Subject.Should().Be(userId.ToString());
            principal.Roles.Should().Contain("finance");
            principal.Scopes.Should().Contain("finance:read");
            principal.Scopes.Should().NotContain("audit:read"); // finance role does not grant it (min-necessary)
            principal.TenantId.Should().Be(tenant);
            principal.ProviderId.Should().Be(providerId.ToString());
            principal.MfaSatisfied.Should().BeFalse("the minimal login is single-factor; 17.3 adds TOTP/amr=otp");
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Mersal.Identity.Infrastructure.IdentityStoreDbContext>();
            var u = await db.Users.FindAsync(userId);
            if (u is not null) { db.Users.Remove(u); await db.SaveChangesAsync(); }
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static async Task<string> PostToken(HttpClient client, Dictionary<string, string> form)
    {
        var resp = await client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        var body = await resp.Content.ReadAsStringAsync();
        resp.IsSuccessStatusCode.Should().BeTrue($"token request should succeed, got {(int)resp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>Validate exactly as a service would: OIDC discovery → JWKS → RS256 + aud, then build the
    /// HbmpPrincipal. (Discovery is done by hand off the same test client so the whole path is exercised.)</summary>
    private static async Task<HbmpPrincipal> ValidateAsync(Factory factory, HttpClient client, string token)
    {
        using var discovery = JsonDocument.Parse(
            await client.GetStringAsync("http://localhost/.well-known/openid-configuration"));
        var issuer = discovery.RootElement.GetProperty("issuer").GetString();
        var jwksUri = discovery.RootElement.GetProperty("jwks_uri").GetString();

        var keySet = new JsonWebKeySet(await client.GetStringAsync(jwksUri));
        var keys = keySet.GetSigningKeys();
        keys.Should().NotBeEmpty("the issuer must publish its RS256 signing key via JWKS");

        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = IdentityContract.ApiResource,
            IssuerSigningKeys = keys,
            ValidateLifetime = true,
            NameClaimType = "sub",
            RoleClaimType = "roles",
        });
        result.IsValid.Should().BeTrue(result.Exception?.Message ?? "token should validate");
        return HbmpPrincipal.FromClaims(new ClaimsPrincipal(result.ClaimsIdentity));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
