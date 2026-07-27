using FluentAssertions;

namespace Mersal.Identity.Tests;

/// <summary>
/// 17.2 issuer conformance (docs/security/token-contract.md §6). Proves tokens minted by the OpenIddict
/// issuer validate through the SAME machinery libs/auth uses (JWKS discovery, RS256, aud=hbmp-api) and yield
/// the expected <c>HbmpPrincipal</c> — for client-credentials, the SPA auth-code+PKCE flow, and a TOTP
/// second factor evidencing MFA. Env-gated on IDENTITY_TEST_DB. DB-less CI skips.
/// </summary>
[Collection("identity-db")]
public class IssuerConformanceTests
{
    [SkippableFact]
    public async Task Client_credentials_token_validates_through_libs_auth()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — issuer integration test skipped.");
        using var factory = new IdentityAppFactory();
        var client = factory.CreateClient();

        var token = await TestFlow.ClientCredentialsToken(
            client, IdentityContractRef.ServiceClientId, IdentityAppFactory.ServiceSecret,
            // 18.B1 narrowed the service client to ServiceScopes only: a background worker ingests events
            // and rebuilds projections, it is never a clinician or an admin. finance:read is NOT one of
            // them, so requesting it here would (correctly) be refused by the client's permissions.
            "notification:ingest reporting:project");

        var principal = await TestFlow.Validate(client, token);
        principal.Subject.Should().Be(IdentityContractRef.ServiceClientId);
        principal.Scopes.Should().BeEquivalentTo(["notification:ingest", "reporting:project"]);
        // The blast-radius assertion 18.B1 exists for: this token reaches ingest and projection surfaces and
        // NOTHING clinical or administrative, so a leaked service secret is not a platform-wide PHI token.
        principal.Scopes.Should().NotContain("finance:read").And.NotContain("emr:read").And.NotContain("admin:write");
    }

    [SkippableFact]
    public async Task Authorization_code_pkce_token_carries_the_frozen_user_claims()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — issuer integration test skipped.");
        using var factory = new IdentityAppFactory();
        var provider = Guid.NewGuid();
        var uname = $"conf-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(factory, uname, "Passw0rd!Mersal", ["finance"], providerId: provider);

        try
        {
            var client = factory.CreateClient();
            var token = await TestFlow.AuthCodeToken(factory, uname, "Passw0rd!Mersal", null,
                "openid finance:read audit:read offline_access");

            var principal = await TestFlow.Validate(client, token);
            principal.Subject.Should().Be(id.ToString());
            principal.Roles.Should().Contain("finance");
            principal.Scopes.Should().Contain("finance:read");
            principal.Scopes.Should().NotContain("audit:read"); // min-necessary: finance does not grant it
            principal.TenantId.Should().Be(TestFlow.TenantA);
            principal.ProviderId.Should().Be(provider.ToString());
            principal.MfaSatisfied.Should().BeFalse("single-factor login; 17.3 TOTP adds amr=otp");
        }
        finally { await TestFlow.DeleteUser(factory, id); }
    }

    [SkippableFact]
    public async Task Totp_two_factor_session_satisfies_MFA_on_the_token()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — issuer integration test skipped.");
        using var factory = new IdentityAppFactory();
        var uname = $"mfa-{Guid.NewGuid():N}";
        var (id, key) = await TestFlow.SeedUser(factory, uname, "Passw0rd!Mersal", ["medical_approval"], twoFactor: true);

        try
        {
            var client = factory.CreateClient();
            var token = await TestFlow.AuthCodeToken(factory, uname, "Passw0rd!Mersal", key,
                "openid auth:decide offline_access");

            var principal = await TestFlow.Validate(client, token);
            principal.Amr.Should().Contain("otp");
            principal.MfaSatisfied.Should().BeTrue("a completed TOTP second factor must evidence MFA");
        }
        finally { await TestFlow.DeleteUser(factory, id); }
    }
}

/// <summary>Alias so the tests read against a stable name for the frozen client/audience ids.</summary>
internal static class IdentityContractRef
{
    public const string ServiceClientId = Mersal.Identity.Domain.IdentityContract.ServiceClientId;
    public const string WebClientId = Mersal.Identity.Domain.IdentityContract.WebClientId;
}
