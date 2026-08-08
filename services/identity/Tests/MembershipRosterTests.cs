using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Identity.Tests;

/// <summary>
/// 21.6 — the membership roster and detail behind the admin screens (design 40 §1, §6).
///
/// The read is tenant-pinned, and the interesting assertion is what happens when it is NOT: a caller asking
/// for another tenant is refused and audited, never silently narrowed to their own. Silently rewriting the
/// filter would render a page of the caller's own tenant under another tenant's heading, and an
/// administrator would review the wrong organisation while believing they had reviewed the right one.
///
/// Env-gated on IDENTITY_TEST_DB. DB-less CI skips.
/// </summary>
[Collection("identity-db")]
public class MembershipRosterTests(IdentityHostFixture host) : IClassFixture<IdentityHostFixture>
{
    private const string Password = "Passw0rd!Mersal";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    private const string AdminScopes = "openid admin:read admin:write offline_access";

    /// <summary>An administrative client. The TOTP key is required, not optional: every admin scope on this
    /// platform is gated on an MFA session, so a token minted without one is refused at the pipeline before
    /// any handler sees it.</summary>
    private static async Task<HttpClient> AdminClient(IdentityAppFactory factory, string uname, string? totpKey)
    {
        var token = await TestFlow.AuthCodeToken(factory, uname, Password, totpKey, AdminScopes);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task SetPlatformAdmin(IdentityAppFactory factory, Guid userId, bool value)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
        var u = await db.Users.FirstAsync(x => x.Id == userId);
        u.IsPlatformAdmin = value;
        await db.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task The_roster_lists_the_callers_own_tenant_with_roles_and_override_counts()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        var factory = host.Factory;
        var admin = $"ros-{Guid.NewGuid():N}";
        var (adminId, key) = await TestFlow.SeedUser(factory, admin, Password, ["super_admin"], twoFactor: true);
        var (subjectId, _) = await TestFlow.SeedUser(factory, $"rossub-{Guid.NewGuid():N}", Password, ["doctor"]);

        try
        {
            var client = await AdminClient(factory, admin, key);
            var membershipId = await TestFlow.MembershipIdOf(factory, subjectId, TestFlow.TenantA);

            // Give the subject one live override so the roster's count column has something to be wrong about.
            (await client.PostAsJsonAsync($"/identity/admin/memberships/{membershipId}/overrides",
                new { scopeKey = "emr:read", effect = "Deny", reason = "under review" }))
                .IsSuccessStatusCode.Should().BeTrue();

            var resp = await client.GetAsync($"/identity/admin/memberships?tenant={TestFlow.TenantA}");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            var rows = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            var row = rows.EnumerateArray().Single(r => r.GetProperty("membershipId").GetGuid() == membershipId);

            row.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("name").GetString())
                .Should().Contain("doctor");
            row.GetProperty("overrideCount").GetInt32().Should().Be(1);
            row.GetProperty("expiredOverrideCount").GetInt32().Should().Be(0);
            row.GetProperty("status").GetString().Should().Be(nameof(MembershipStatus.Active));
        }
        finally
        {
            await TestFlow.DeleteUser(factory, adminId);
            await TestFlow.DeleteUser(factory, subjectId);
        }
    }

    [SkippableFact]
    public async Task Asking_for_another_tenant_is_refused_not_quietly_narrowed()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        var factory = host.Factory;
        var admin = $"rosx-{Guid.NewGuid():N}";
        var (adminId, key) = await TestFlow.SeedUser(factory, admin, Password, ["super_admin"], twoFactor: true);

        try
        {
            var client = await AdminClient(factory, admin, key);
            var resp = await client.GetAsync($"/identity/admin/memberships?tenant={TenantB}");

            // THE POINT: 403, not a 200 full of the caller's own tenant. A2 — nothing security-relevant is
            // silent, and a silently-narrowed filter is a wrong answer wearing a correct one's clothes.
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await resp.Content.ReadAsStringAsync()).Should().Contain("cross-tenant-read-denied");
        }
        finally
        {
            await TestFlow.DeleteUser(factory, adminId);
        }
    }

    [SkippableFact]
    public async Task The_platform_admin_flag_widens_the_roster_and_nothing_else()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        var factory = host.Factory;
        var admin = $"rosp-{Guid.NewGuid():N}";
        var (adminId, key) = await TestFlow.SeedUser(factory, admin, Password, ["super_admin"], twoFactor: true);
        var (subjectId, _) = await TestFlow.SeedUser(factory, $"rospsub-{Guid.NewGuid():N}", Password, ["doctor"]);
        await TestFlow.SeedMembership(factory, subjectId, TenantB, ["doctor"]);
        await SetPlatformAdmin(factory, adminId, true);

        try
        {
            var client = await AdminClient(factory, admin, key);
            var resp = await client.GetAsync($"/identity/admin/memberships?tenant={TenantB}");

            // A1: the flag buys ADMINISTRATIVE reach. A membership roster is administrative data — names,
            // roles, statuses — and contains no clinical field by construction, so widening it here does not
            // widen anything the min-necessary rules govern.
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            var rows = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            rows.EnumerateArray().Should().Contain(r => r.GetProperty("tenantId").GetString() == TenantB);

            // And the payload carries nothing clinical — the structural half of the same claim.
            var body = await resp.Content.ReadAsStringAsync();
            foreach (var forbidden in new[] { "diagnos", "prescription", "labresult", "encounter", "note" })
                body.ToUpperInvariant().Should().NotContain(forbidden.ToUpperInvariant());
        }
        finally
        {
            await TestFlow.DeleteUser(factory, adminId);
            await TestFlow.DeleteUser(factory, subjectId);
        }
    }

    [SkippableFact]
    public async Task A_lapsed_override_is_reported_as_expired_rather_than_dropped()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        var factory = host.Factory;
        var admin = $"rose-{Guid.NewGuid():N}";
        var (adminId, key) = await TestFlow.SeedUser(factory, admin, Password, ["super_admin"], twoFactor: true);
        var (subjectId, _) = await TestFlow.SeedUser(factory, $"rosesub-{Guid.NewGuid():N}", Password, ["doctor"]);

        try
        {
            var client = await AdminClient(factory, admin, key);
            var membershipId = await TestFlow.MembershipIdOf(factory, subjectId, TestFlow.TenantA);

            (await client.PostAsJsonAsync($"/identity/admin/memberships/{membershipId}/overrides", new
            {
                scopeKey = "emr:read", effect = "Allow", reason = "ramadan surge cover",
                validUntil = DateTimeOffset.UtcNow.AddDays(-1),
            })).IsSuccessStatusCode.Should().BeTrue();

            var resp = await client.GetAsync($"/identity/admin/memberships/{membershipId}");
            var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            var ovr = body.GetProperty("overrides").EnumerateArray().Single();

            // Listed, flagged expired — the evaluator already ignores it, and dropping it here would leave
            // an administrator unable to explain why this person lost the key overnight.
            ovr.GetProperty("expired").GetBoolean().Should().BeTrue();
            ovr.GetProperty("reason").GetString().Should().Be("ramadan surge cover");
            body.GetProperty("expiredOverrideCount").GetInt32().Should().Be(1);
        }
        finally
        {
            await TestFlow.DeleteUser(factory, adminId);
            await TestFlow.DeleteUser(factory, subjectId);
        }
    }
}
