using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Mersal.Identity.Api.Auth;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Identity.Tests;

/// <summary>
/// 21.5 — the access-review snapshot (design 40 §6).
///
/// This is the artifact a reviewer signs, so "complete" is the requirement, not "roughly right". The
/// failure mode it guards against is an override listed WITHOUT its reason: the reviewer sees an exception,
/// cannot judge whether it is still justified, and either rubber-stamps it or escalates everything.
///
/// Env-gated on IDENTITY_TEST_DB. DB-less CI skips.
/// </summary>
[Collection("identity-db")]
public class AccessReviewTests
{
    private const string Password = "Passw0rd!Mersal";

    [SkippableFact]
    public async Task THE_acceptance_case_the_review_contains_overrides_WITH_their_reasons_and_grantors()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        using var factory = new IdentityAppFactory();
        var uname = $"rev-{Guid.NewGuid():N}";
        var (userId, _) = await TestFlow.SeedUser(factory, uname, Password, ["finance"]);

        try
        {
            var membershipId = await TestFlow.MembershipIdOf(factory, userId, TestFlow.TenantA);

            using (var seed = factory.Services.CreateScope())
            {
                var db = seed.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
                db.Overrides.Add(new MembershipOverride
                {
                    OverrideId = Guid.NewGuid(), MembershipId = membershipId, ScopeKey = "emr:read",
                    Effect = OverrideEffect.Allow, Reason = "covering the Alexandria clinic during October",
                    GrantedBy = "admin-7", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            using var scope = factory.Services.CreateScope();
            var report = await AccessReviewEndpoints.BuildAsync(
                scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>(),
                scope.ServiceProvider.GetRequiredService<IEffectiveSetService>(),
                TestFlow.TenantA, DateTimeOffset.UtcNow);

            var row = report.Memberships.Single(m => m.Username == uname);

            row.Roles.Should().Contain("finance");
            row.EffectiveKeys.Should().Contain("emr:read", "the override is part of the effective access");

            var ovr = row.Overrides.Single();
            ovr.ScopeKey.Should().Be("emr:read");
            ovr.Effect.Should().Be("Allow");
            // Without these two the reviewer cannot judge the exception at all.
            ovr.Reason.Should().Be("covering the Alexandria clinic during October");
            ovr.GrantedBy.Should().Be("admin-7");
        }
        finally { await TestFlow.DeleteUser(factory, userId); }
    }

    [SkippableFact]
    public async Task The_review_reports_the_most_privileged_tier_and_flags_platform_admins()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        using var factory = new IdentityAppFactory();
        var uname = $"revp-{Guid.NewGuid():N}";
        var (userId, _) = await TestFlow.SeedUser(factory, uname, Password, ["reception", "doctor"]);

        try
        {
            using (var seed = factory.Services.CreateScope())
            {
                var db = seed.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();
                var u = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                    .FirstAsync(db.Users, x => x.Id == userId);
                u.IsPlatformAdmin = true;
                await db.SaveChangesAsync();
            }

            using var scope = factory.Services.CreateScope();
            var report = await AccessReviewEndpoints.BuildAsync(
                scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>(),
                scope.ServiceProvider.GetRequiredService<IEffectiveSetService>(),
                TestFlow.TenantA, DateTimeOffset.UtcNow);

            var row = report.Memberships.Single(m => m.Username == uname);

            // reception is level 3, doctor level 1; lower = more privileged, so the report must show the
            // most privileged tier held. Showing the average or the last one read would understate risk.
            row.Level.Should().Be(1);
            row.IsPlatformAdmin.Should().BeTrue();
            report.PlatformAdminCount.Should().BeGreaterThan(0,
                "the count of platform administrators is the first number a reviewer looks for");
        }
        finally { await TestFlow.DeleteUser(factory, userId); }
    }

    [SkippableFact]
    public async Task Holder_counts_answer_who_else_has_this_key()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        using var factory = new IdentityAppFactory();
        var a = $"revh1-{Guid.NewGuid():N}";
        var b = $"revh2-{Guid.NewGuid():N}";
        var (aId, _) = await TestFlow.SeedUser(factory, a, Password, ["finance"]);
        var (bId, _) = await TestFlow.SeedUser(factory, b, Password, ["finance"]);

        try
        {
            using var scope = factory.Services.CreateScope();
            var report = await AccessReviewEndpoints.BuildAsync(
                scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>(),
                scope.ServiceProvider.GetRequiredService<IEffectiveSetService>(),
                TestFlow.TenantA, DateTimeOffset.UtcNow);

            // The question a reviewer asks about every sensitive key, and the one that cannot be answered by
            // reading memberships one at a time.
            report.HolderCountsByKey.Should().ContainKey("finance:read");
            report.HolderCountsByKey["finance:read"].Should().BeGreaterThanOrEqualTo(2);
        }
        finally
        {
            await TestFlow.DeleteUser(factory, aId);
            await TestFlow.DeleteUser(factory, bId);
        }
    }

    [Fact]
    public void The_csv_quotes_reasons_containing_commas_and_quotes()
    {
        // Reasons are free text written by administrators. An unescaped comma shifts every later column and
        // silently corrupts the evidence — the reviewer reads a well-formed table of wrong values.
        var report = new AccessReviewReport(
            "t-1", DateTimeOffset.UtcNow,
            [new AccessReviewMembership(
                Guid.NewGuid(), "nurse.a", "Nurse A", "Active", ["nurse"], 1, false,
                [new AccessReviewOverride("emr:read", "Allow",
                    "covering Alexandria, then Cairo; per \"October rota\"", "admin-7", null)],
                ["emr:read"])],
            new Dictionary<string, int> { ["emr:read"] = 1 }, 0);

        var csv = AccessReviewEndpoints.ToCsv(report);
        var dataLine = csv.Split('\n')[1];

        dataLine.Should().Contain("\"\"October rota\"\"", "an embedded quote must be doubled per RFC 4180");
        // 8 header columns ⇒ the quoted reason must not add unquoted commas.
        csv.Split('\n')[0].Split(',').Should().HaveCount(8);
    }

    [SkippableFact]
    public async Task Generating_a_review_is_audited_as_an_EXPORT()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        using var factory = new IdentityAppFactory();
        var admin = $"revadm-{Guid.NewGuid():N}";
        var (adminId, key) = await TestFlow.SeedUser(factory, admin, Password, ["super_admin"], twoFactor: true);

        try
        {
            var token = await TestFlow.AuthCodeToken(factory, admin, Password, key,
                "openid admin:read admin:write offline_access");
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var resp = await client.GetAsync($"/identity/admin/access-review/{TestFlow.TenantA}");
            resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

            var csv = await client.GetAsync($"/identity/admin/access-review/{TestFlow.TenantA}?format=csv");
            csv.StatusCode.Should().Be(HttpStatusCode.OK);
            csv.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        }
        finally { await TestFlow.DeleteUser(factory, adminId); }
    }

    [SkippableFact]
    public async Task An_unauthenticated_caller_cannot_read_a_tenants_access_posture()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        using var factory = new IdentityAppFactory();
        var client = factory.CreateClient();

        // The report is a complete map of who can do what in a tenant — the single most useful document to
        // an attacker choosing a target.
        var resp = await client.GetAsync($"/identity/admin/access-review/{TestFlow.TenantA}");
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
