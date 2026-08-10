using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Identity.Tests;

/// <summary>17.4 — the in-app admin surface (C3). Proves user/role administration requires a bearer token
/// with the admin scope AND an MFA session, and that a create is applied + role-assigned. Env-gated.</summary>
[Collection("identity-db")]
public class AdminApiTests(IdentityHostFixture host) : IClassFixture<IdentityHostFixture>
{
    [SkippableFact]
    public async Task Mfa_admin_can_create_a_user_and_assign_roles()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — admin integration test skipped.");
        var factory = host.Factory;
        var admin = $"admin-{Guid.NewGuid():N}";
        var (adminId, key) = await TestFlow.SeedUser(factory, admin, "Passw0rd!Mersal", ["super_admin"], twoFactor: true);
        Guid? createdId = null;
        try
        {
            var token = await TestFlow.AuthCodeToken(factory, admin, "Passw0rd!Mersal", key,
                "openid admin:read admin:write offline_access");
            var client = Authed(factory, token);

            var newUser = $"nurse-{Guid.NewGuid():N}";
            var resp = await client.PostAsJsonAsync("/identity/admin/users", new
            {
                username = newUser, displayName = "New Nurse", password = "Passw0rd!Mersal",
                email = $"{newUser}@example.org",
                tenantId = TestFlow.TenantA, roles = new[] { "nurse" },
            });
            resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());

            // Verify it landed with the role.
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Mersal.Identity.Infrastructure.IdentityStoreDbContext>();
            var norm = newUser.ToUpperInvariant();
            var u = await db.Users.FirstAsync(x => x.NormalizedUserName == norm);
            createdId = u.Id;
            u.IsActive.Should().BeTrue();
        }
        finally
        {
            if (createdId is { } cid) await TestFlow.DeleteUser(factory, cid);
            await TestFlow.DeleteUser(factory, adminId);
        }
    }

    [SkippableFact]
    public async Task Non_mfa_admin_token_is_rejected()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — admin integration test skipped.");
        var factory = host.Factory;
        var admin = $"admin2-{Guid.NewGuid():N}";
        // org_admin grants admin:write but this account has NO second factor → amr=pwd only.
        var (adminId, _) = await TestFlow.SeedUser(factory, admin, "Passw0rd!Mersal", ["org_admin"], twoFactor: false);
        try
        {
            var token = await TestFlow.AuthCodeToken(factory, admin, "Passw0rd!Mersal", null,
                "openid admin:read admin:write offline_access");
            var client = Authed(factory, token);

            var resp = await client.PostAsJsonAsync("/identity/admin/users", new
            {
                username = "x", displayName = "x", password = "Passw0rd!Mersal", email = "x@example.org",
                tenantId = TestFlow.TenantA, roles = new[] { "nurse" },
            });
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "admin actions require an MFA (step-up) session");
        }
        finally { await TestFlow.DeleteUser(factory, adminId); }
    }

    [SkippableFact]
    public async Task Token_without_admin_scope_is_rejected()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — admin integration test skipped.");
        var factory = host.Factory;
        var user = $"fin-{Guid.NewGuid():N}";
        var (id, key) = await TestFlow.SeedUser(factory, user, "Passw0rd!Mersal", ["finance"], twoFactor: true);
        try
        {
            // MFA session, but finance grants no admin scope.
            var token = await TestFlow.AuthCodeToken(factory, user, "Passw0rd!Mersal", key, "openid finance:read offline_access");
            var client = Authed(factory, token);

            var resp = await client.GetAsync("/identity/admin/users");
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the admin surface requires an admin scope");
        }
        finally { await TestFlow.DeleteUser(factory, id); }
    }

    private static HttpClient Authed(IdentityAppFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
