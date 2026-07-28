using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Mersal.Identity.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Identity.Tests;

/// <summary>
/// 21.2 — the override admin surface (design 40 §2).
///
/// This is the most dangerous endpoint in the service: a way to give one person a key their role does not
/// carry. These tests pin the constraints that make it safe to have at all — a mandatory reason, SoD vetting
/// identical to a role grant, an audited refusal rather than a quiet bypass, and mode-2 invalidation so the
/// out-of-session evaluator never serves authority that was just changed.
///
/// Env-gated on IDENTITY_TEST_DB. DB-less CI skips.
/// </summary>
[Collection("identity-db")]
public class OverrideAdminApiTests
{
    private const string Password = "Passw0rd!Mersal";
    private const string AdminScopes = "openid admin:read admin:write offline_access";

    [SkippableFact]
    public async Task An_allow_override_reaches_the_effective_set_and_the_next_token()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — admin integration test skipped.");
        using var factory = new IdentityAppFactory();
        var admin = $"ovadm-{Guid.NewGuid():N}";
        var subject = $"ovsub-{Guid.NewGuid():N}";
        var (adminId, key) = await TestFlow.SeedUser(factory, admin, Password, ["super_admin"], twoFactor: true);
        var (subjectId, _) = await TestFlow.SeedUser(factory, subject, Password, ["reception"]);

        try
        {
            var client = Authed(factory, await TestFlow.AuthCodeToken(factory, admin, Password, key, AdminScopes));
            var membershipId = await TestFlow.MembershipIdOf(factory, subjectId, TestFlow.TenantA);

            var resp = await client.PostAsJsonAsync($"/identity/admin/memberships/{membershipId}/overrides", new
            {
                scopeKey = "finance:read", effect = "Allow", reason = "covering finance during leave",
            });
            resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

            // Mode 2 — the admin "what would this person see" view.
            var preview = await client.GetFromJsonAsync<EffectiveView>(
                $"/identity/admin/memberships/{membershipId}/effective");
            preview!.Scopes.Should().Contain("finance:read");

            // Mode 1 — and it must reach a freshly issued token, not just the preview.
            var token = await TestFlow.AuthCodeToken(factory, subject, Password, null, "openid finance:read");
            (await TestFlow.Validate(factory.CreateClient(), token)).Scopes.Should().Contain("finance:read");
        }
        finally
        {
            await TestFlow.DeleteUser(factory, adminId);
            await TestFlow.DeleteUser(factory, subjectId);
        }
    }

    [SkippableFact]
    public async Task An_override_that_would_breach_segregation_of_duties_is_refused_with_the_conflict()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — admin integration test skipped.");
        using var factory = new IdentityAppFactory();
        var admin = $"ovadm2-{Guid.NewGuid():N}";
        var subject = $"ovsub2-{Guid.NewGuid():N}";
        var (adminId, key) = await TestFlow.SeedUser(factory, admin, Password, ["super_admin"], twoFactor: true);
        // network_team sets the rates. Handing it payment RELEASE is "rate manipulation + self-pay" from
        // 10-role-matrix §7 — and unlike `finance`, it does not already imply the release half, so this is a
        // conflict the override genuinely introduces rather than a pre-existing one it merely names.
        var (subjectId, _) = await TestFlow.SeedUser(factory, subject, Password, ["network_team"]);

        try
        {
            var client = Authed(factory, await TestFlow.AuthCodeToken(factory, admin, Password, key, AdminScopes));
            var membershipId = await TestFlow.MembershipIdOf(factory, subjectId, TestFlow.TenantA);

            var resp = await client.PostAsJsonAsync($"/identity/admin/memberships/{membershipId}/overrides", new
            {
                scopeKey = "finance:approve", effect = "Allow", reason = "they asked",
            });

            // 409 with the reason, not a bypass. An exception path that skipped SoD would simply BE the
            // supported way to hold both halves of a split duty.
            resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var body = await resp.Content.ReadAsStringAsync();
            body.Should().Contain("sod-conflict");
            body.Should().Contain("Rate manipulation",
                "the refusal must say WHICH duty it protects, or it cannot be acted on");

            // And nothing was written — a refused grant must not be half-applied.
            using var scope = factory.Services.CreateScope();
            var effective = scope.ServiceProvider.GetRequiredService<IEffectiveSetService>();
            (await effective.ForMembershipAsync(membershipId))!.Has("finance:approve").Should().BeFalse();
        }
        finally
        {
            await TestFlow.DeleteUser(factory, adminId);
            await TestFlow.DeleteUser(factory, subjectId);
        }
    }

    [SkippableFact]
    public async Task A_deny_override_is_never_blocked_by_segregation_of_duties()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — admin integration test skipped.");
        using var factory = new IdentityAppFactory();
        var admin = $"ovadm3-{Guid.NewGuid():N}";
        var subject = $"ovsub3-{Guid.NewGuid():N}";
        var (adminId, key) = await TestFlow.SeedUser(factory, admin, Password, ["super_admin"], twoFactor: true);
        var (subjectId, _) = await TestFlow.SeedUser(factory, subject, Password, ["network_team"]);

        try
        {
            var client = Authed(factory, await TestFlow.AuthCodeToken(factory, admin, Password, key, AdminScopes));
            var membershipId = await TestFlow.MembershipIdOf(factory, subjectId, TestFlow.TenantA);

            // The same key that was refused as an Allow above. Taking authority AWAY cannot create a
            // forbidden combination, and blocking it would make SoD a reason you cannot reduce someone's
            // access — the exact opposite of what it is for.
            var resp = await client.PostAsJsonAsync($"/identity/admin/memberships/{membershipId}/overrides", new
            {
                scopeKey = "finance:approve", effect = "Deny", reason = "under investigation",
            });
            resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

            var preview = await client.GetFromJsonAsync<EffectiveView>(
                $"/identity/admin/memberships/{membershipId}/effective");
            preview!.Scopes.Should().NotContain("finance:approve");
        }
        finally
        {
            await TestFlow.DeleteUser(factory, adminId);
            await TestFlow.DeleteUser(factory, subjectId);
        }
    }

    [SkippableFact]
    public async Task Revoking_an_override_invalidates_the_out_of_session_cache_immediately()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — admin integration test skipped.");
        using var factory = new IdentityAppFactory();
        var admin = $"ovadm4-{Guid.NewGuid():N}";
        var subject = $"ovsub4-{Guid.NewGuid():N}";
        var (adminId, key) = await TestFlow.SeedUser(factory, admin, Password, ["super_admin"], twoFactor: true);
        var (subjectId, _) = await TestFlow.SeedUser(factory, subject, Password, ["reception"]);

        try
        {
            var client = Authed(factory, await TestFlow.AuthCodeToken(factory, admin, Password, key, AdminScopes));
            var membershipId = await TestFlow.MembershipIdOf(factory, subjectId, TestFlow.TenantA);

            await client.PostAsJsonAsync($"/identity/admin/memberships/{membershipId}/overrides", new
            {
                scopeKey = "finance:read", effect = "Allow", reason = "temporary cover",
            });
            // Read it back, so the mode-2 cache is definitely POPULATED before the revocation. Without this
            // the test would pass against a service that never caches and never invalidates.
            (await client.GetFromJsonAsync<EffectiveView>($"/identity/admin/memberships/{membershipId}/effective"))!
                .Scopes.Should().Contain("finance:read");

            var del = await client.DeleteAsync($"/identity/admin/memberships/{membershipId}/overrides/finance:read");
            del.StatusCode.Should().Be(HttpStatusCode.OK, await del.Content.ReadAsStringAsync());

            // Immediately, not in 60 seconds. The TTL is the backstop for anything that forgets to
            // invalidate; serving withdrawn authority for a further minute is an authorization decision made
            // on access that no longer exists.
            (await client.GetFromJsonAsync<EffectiveView>($"/identity/admin/memberships/{membershipId}/effective"))!
                .Scopes.Should().NotContain("finance:read");
        }
        finally
        {
            await TestFlow.DeleteUser(factory, adminId);
            await TestFlow.DeleteUser(factory, subjectId);
        }
    }

    [SkippableFact]
    public async Task An_override_without_a_reason_is_refused()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — admin integration test skipped.");
        using var factory = new IdentityAppFactory();
        var admin = $"ovadm5-{Guid.NewGuid():N}";
        var subject = $"ovsub5-{Guid.NewGuid():N}";
        var (adminId, key) = await TestFlow.SeedUser(factory, admin, Password, ["super_admin"], twoFactor: true);
        var (subjectId, _) = await TestFlow.SeedUser(factory, subject, Password, ["reception"]);

        try
        {
            var client = Authed(factory, await TestFlow.AuthCodeToken(factory, admin, Password, key, AdminScopes));
            var membershipId = await TestFlow.MembershipIdOf(factory, subjectId, TestFlow.TenantA);

            var resp = await client.PostAsJsonAsync($"/identity/admin/memberships/{membershipId}/overrides", new
            {
                scopeKey = "finance:read", effect = "Allow", reason = "   ",
            });
            resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        }
        finally
        {
            await TestFlow.DeleteUser(factory, adminId);
            await TestFlow.DeleteUser(factory, subjectId);
        }
    }

    private sealed record EffectiveView(Guid MembershipId, string[] Scopes);

    private static HttpClient Authed(IdentityAppFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
