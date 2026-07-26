using FluentAssertions;
using Mersal.Admin.Api;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Tests;

/// <summary>Phase 14.2 branch assignment at the datastore (env-gated <c>ADMIN_TEST_DB</c>, live PG with
/// migration 0004 applied). Proves the design-37 §2.2–2.3 acceptance: a second active Home is rejected
/// (409 home-exists); Home ∪ Additional resolves the permitted set; a revoked assignment drops out of the
/// permitted set on the next request; an in-set switch emits ActiveBranchSwitched while an out-of-set switch
/// is denied + audited BranchScopeDenied. Each test scopes to a unique tenant and self-cleans.</summary>
[Collection("admin-db")]
public class BranchAssignmentIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("ADMIN_TEST_DB");
    private static AdminDbContext Ctx() =>
        new(new DbContextOptionsBuilder<AdminDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static (BranchAssignmentService, InMemoryOutbox, InMemoryAuditOutbox) Build(AdminDbContext db)
    {
        var events = new InMemoryOutbox();
        var auditOut = new InMemoryAuditOutbox();
        var audit = new AuditClient(auditOut, new AuditClientContext("admin-test"), TimeProvider.System);
        return (new BranchAssignmentService(db, audit, events, TimeProvider.System), events, auditOut);
    }

    private static readonly ActorContext Admin = new("admin-1", "org_admin", null, Mfa: true);
    private static readonly DateOnly From = new(2026, 1, 1);

    [SkippableFact]
    public async Task A_second_active_home_is_rejected_and_the_permitted_set_is_home_union_additional()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        var subject = "user-" + Guid.NewGuid().ToString("N")[..8];
        var maadi = Guid.NewGuid();
        var dokki = Guid.NewGuid();
        var aswan = Guid.NewGuid();
        try
        {
            await using var db = Ctx();
            var (svc, events, _) = Build(db);

            (await svc.AssignAsync(Admin, tenant, subject, maadi, BranchAssignmentType.Home, From, null)).Ok.Should().BeTrue();
            (await svc.AssignAsync(Admin, tenant, subject, dokki, BranchAssignmentType.Additional, From, null)).Ok.Should().BeTrue();

            // A second active Home breaches the one-home invariant.
            var second = await svc.AssignAsync(Admin, tenant, subject, aswan, BranchAssignmentType.Home, From, null);
            second.Ok.Should().BeFalse();
            second.ReasonCode.Should().Be("home-exists");

            var res = await svc.ResolveAsync(tenant, subject, requested: null);
            res.BranchId.Should().Be(maadi, "no header resolves to Home");
            res.Permitted.Should().BeEquivalentTo([maadi, dokki]);
            events.AllMessages.Should().Contain(m => m.EventType == "UserBranchAssigned");
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task A_revoked_assignment_drops_out_of_the_permitted_set_on_the_next_request()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        var subject = "user-" + Guid.NewGuid().ToString("N")[..8];
        var maadi = Guid.NewGuid();
        var dokki = Guid.NewGuid();
        try
        {
            await using var db = Ctx();
            var (svc, _, _) = Build(db);
            await svc.AssignAsync(Admin, tenant, subject, maadi, BranchAssignmentType.Home, From, null);
            var add = await svc.AssignAsync(Admin, tenant, subject, dokki, BranchAssignmentType.Additional, From, null);

            (await svc.ResolveAsync(tenant, subject, requested: dokki)).Allowed.Should().BeTrue();

            (await svc.RevokeAsync(Admin, tenant, add.Assignment!.AssignmentId)).Should().BeTrue();

            var after = await svc.ResolveAsync(tenant, subject, requested: dokki);
            after.Allowed.Should().BeFalse("the revoked branch is no longer permitted");
            after.Permitted.Should().BeEquivalentTo([maadi]);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Switching_to_a_non_permitted_branch_is_denied_and_audited_but_an_in_set_switch_is_recorded()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        var subject = "user-" + Guid.NewGuid().ToString("N")[..8];
        var maadi = Guid.NewGuid();
        var dokki = Guid.NewGuid();
        var aswan = Guid.NewGuid();
        try
        {
            await using var db = Ctx();
            var (svc, events, auditOut) = Build(db);
            await svc.AssignAsync(Admin, tenant, subject, maadi, BranchAssignmentType.Home, From, null);
            await svc.AssignAsync(Admin, tenant, subject, dokki, BranchAssignmentType.Additional, From, null);

            var denied = await svc.SwitchAsync(Admin, tenant, subject, aswan);
            denied.Allowed.Should().BeFalse();
            auditOut.Events.Should().Contain(e => e.DecisionOutcome == "BranchScopeDenied" && e.Severity == AuditSeverity.High);

            var ok = await svc.SwitchAsync(Admin, tenant, subject, dokki);
            ok.Allowed.Should().BeTrue();
            ok.BranchId.Should().Be(dokki);
            events.AllMessages.Should().Contain(m => m.EventType == "ActiveBranchSwitched");
        }
        finally { await Cleanup(tenant); }
    }

    private static string T() => "t-" + Guid.NewGuid().ToString("N")[..10];

    private static async Task Cleanup(string tenant)
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM admin.user_branch_assignment WHERE tenant_id = {0}", tenant);
    }
}
