using FluentAssertions;
using Mersal.Admin.Api;
using Mersal.Admin.Domain;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Authz;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mersal.Admin.Tests;

/// <summary>
/// 21.4 — the CAPS at their call site (design 40 §4).
///
/// <see cref="ProgramLimitConcurrencyTests"/> already proves the mechanism (the advisory lock, the live count
/// under parallel writes). What this file pins is the SEMANTICS of the wiring, which is where a cap is easy to
/// get quietly wrong in either direction: too strict and a tenant at its cap can no longer adjust the roles of
/// the people it already has; too loose and the cap counts nothing. Both failures are invisible from the
/// outside — one looks like a bug in role administration, the other like a working limit.
///
/// Env-gated on ADMIN_TEST_DB. DB-less CI skips.
/// </summary>
[Collection("admin-db")]
public class ProgramCapTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("ADMIN_TEST_DB");

    private static AdminDbContext Ctx() =>
        new(new DbContextOptionsBuilder<AdminDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static readonly ActorContext Actor = new("admin-1", "super_admin", "t-1", Mfa: true);

    private static RoleAdminService Service(AdminDbContext db) =>
        new(db, new AuditClient(new InMemoryAuditOutbox(), new AuditClientContext("admin-service"), TimeProvider.System),
            TimeProvider.System, new TenantProgramStore(db));

    private static string NewTenant() => "t-cap-" + Guid.NewGuid().ToString("N")[..12];
    private static string NewUser() => "u-" + Guid.NewGuid().ToString("N")[..10];

    private static Task SetCapAsync(AdminDbContext db, string tenant, string key, int max) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO admin.tenant_limit (tenant_id, limit_key, max_value)
            VALUES ({0}, {1}, {2})
            ON CONFLICT (tenant_id, limit_key) DO UPDATE SET max_value = {2}
            """, [tenant, key, max]);

    private static async Task CleanupAsync(AdminDbContext db, string tenant)
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM admin.role_binding WHERE tenant_id = {0}", [tenant]);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM admin.tenant_limit WHERE tenant_id = {0}", [tenant]);
    }

    [SkippableFact]
    public async Task A_grant_that_would_exceed_the_user_cap_is_refused_with_the_numbers()
    {
        Skip.If(Db is null, "ADMIN_TEST_DB not set — DB integration test skipped.");
        await using var db = Ctx();
        var svc = Service(db);
        var tenant = NewTenant();

        try
        {
            await SetCapAsync(db, tenant, ProgramLimits.ActiveUsers, 1);

            var first = await svc.GrantAsync(Actor, tenant, NewUser(), "reception", ScopeType.Tenant, null, "onboarding");
            first.Ok.Should().BeTrue("a cap of 1 permits exactly one user");

            var second = await svc.GrantAsync(Actor, tenant, NewUser(), "reception", ScopeType.Tenant, null, "onboarding");

            second.Ok.Should().BeFalse();
            second.ReasonCode.Should().Be(ProgramEnablement.LimitReachedCode);
            // The numbers are the point: "you are at your limit" alone does not tell an administrator whether to
            // free a slot or ask Mersal to raise the cap.
            var problem = second.Problem.Should()
                .BeAssignableTo<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>().Subject;
            problem.ProblemDetails.Extensions["limit"].Should().Be(ProgramLimits.ActiveUsers);
            problem.ProblemDetails.Extensions["max"].Should().Be(1);
            problem.ProblemDetails.Extensions["current"].Should().Be(1);

            // And nothing was written — the refusal rolled back rather than leaving the tenant one over.
            var count = await db.Database.SqlQueryRaw<int>(
                """
                SELECT count(DISTINCT subject_user_id)::int AS "Value"
                FROM admin.role_binding WHERE tenant_id = {0} AND status = 'Active'
                """, tenant).SingleAsync();
            count.Should().Be(1);
        }
        finally { await CleanupAsync(db, tenant); }
    }

    /// <summary>
    /// A cap counts USERS, not bindings. A tenant sitting exactly at its cap must still be able to give one of
    /// its existing people another role — that consumes no slot. Enforcing per-binding would freeze role
    /// administration for every tenant at its limit, which is not what the cap means and would read as a bug.
    /// </summary>
    [SkippableFact]
    public async Task A_second_role_for_an_existing_user_consumes_no_slot()
    {
        Skip.If(Db is null, "ADMIN_TEST_DB not set — DB integration test skipped.");
        await using var db = Ctx();
        var svc = Service(db);
        var tenant = NewTenant();
        var user = NewUser();

        try
        {
            await SetCapAsync(db, tenant, ProgramLimits.ActiveUsers, 1);
            (await svc.GrantAsync(Actor, tenant, user, "reception", ScopeType.Tenant, null, "onboarding"))
                .Ok.Should().BeTrue();

            var second = await svc.GrantAsync(Actor, tenant, user, "call_center", ScopeType.Tenant, null, "extra duty");

            second.Ok.Should().BeTrue("the tenant still has exactly one active user");
        }
        finally { await CleanupAsync(db, tenant); }
    }

    /// <summary>
    /// The provider cap asks a DIFFERENT question of the same grant: a user already active under a tenant-scoped
    /// role still takes a provider slot the first time they are given a provider-scoped one. Sharing one
    /// "is this user new" test between the two caps would let the provider cap be bypassed by granting a
    /// tenant-scoped role first.
    /// </summary>
    [SkippableFact]
    public async Task An_existing_user_still_consumes_a_provider_slot_on_their_first_provider_binding()
    {
        Skip.If(Db is null, "ADMIN_TEST_DB not set — DB integration test skipped.");
        await using var db = Ctx();
        var svc = Service(db);
        var tenant = NewTenant();
        var user = NewUser();

        try
        {
            await SetCapAsync(db, tenant, ProgramLimits.ActiveProviderUsers, 0);
            (await svc.GrantAsync(Actor, tenant, user, "reception", ScopeType.Tenant, null, "onboarding"))
                .Ok.Should().BeTrue("the provider cap says nothing about a tenant-scoped role");

            var asProvider = await svc.GrantAsync(
                Actor, tenant, user, "pharmacist", ScopeType.Provider, Guid.NewGuid().ToString(), "provider duty");

            asProvider.Ok.Should().BeFalse("a cap of 0 permits no provider users at all");
            asProvider.ReasonCode.Should().Be(ProgramEnablement.LimitReachedCode);
        }
        finally { await CleanupAsync(db, tenant); }
    }

    /// <summary>No cap configured means UNLIMITED, not zero. Inventing a default would take every tenant offline
    /// the day admin.tenant_limit shipped empty — which is exactly the state it is in today.</summary>
    [SkippableFact]
    public async Task With_no_cap_configured_grants_are_unlimited()
    {
        Skip.If(Db is null, "ADMIN_TEST_DB not set — DB integration test skipped.");
        await using var db = Ctx();
        var svc = Service(db);
        var tenant = NewTenant();

        try
        {
            for (var i = 0; i < 3; i++)
            {
                (await svc.GrantAsync(Actor, tenant, NewUser(), "reception", ScopeType.Tenant, null, "onboarding"))
                    .Ok.Should().BeTrue();
            }
        }
        finally { await CleanupAsync(db, tenant); }
    }

    /// <summary>Revoking frees the slot immediately — true by construction because the cap counts live rows
    /// rather than maintaining a counter that drifts after the first failed transaction.</summary>
    [SkippableFact]
    public async Task Revoking_a_binding_frees_the_slot_immediately()
    {
        Skip.If(Db is null, "ADMIN_TEST_DB not set — DB integration test skipped.");
        await using var db = Ctx();
        var svc = Service(db);
        var tenant = NewTenant();
        var first = NewUser();

        try
        {
            await SetCapAsync(db, tenant, ProgramLimits.ActiveUsers, 1);
            var granted = await svc.GrantAsync(Actor, tenant, first, "reception", ScopeType.Tenant, null, "onboarding");
            granted.Ok.Should().BeTrue();

            (await svc.GrantAsync(Actor, tenant, NewUser(), "reception", ScopeType.Tenant, null, "onboarding"))
                .Ok.Should().BeFalse("at cap");

            // Through the service, not raw SQL: a revoked row must carry its revocation metadata (the table has
            // a CHECK that says so), and going round the service was the test being wrong, not the cap.
            (await svc.RevokeAsync(Actor, tenant, granted.Binding!.BindingId, "left the organisation"))
                .Should().BeTrue();

            (await svc.GrantAsync(Actor, tenant, NewUser(), "reception", ScopeType.Tenant, null, "onboarding"))
                .Ok.Should().BeTrue("the revoked binding no longer occupies the slot");
        }
        finally { await CleanupAsync(db, tenant); }
    }
}
