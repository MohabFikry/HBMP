using FluentAssertions;
using Mersal.Admin.Api;
using Mersal.Admin.Domain;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Tests;

/// <summary>Break-glass lifecycle + dashboards at the datastore (env-gated <c>ADMIN_TEST_DB</c>, live PG). Proves
/// the phase-8b.3 acceptance criteria: request → dual-control approve → step-up activate → scoped access →
/// auto-expire; a self-approval is rejected; an out-of-scope access is denied (no field-deny bypass); every access
/// emits a HIGH-severity break_glass audit event; and the dashboards are tenant-scoped + audit their own reads.
/// Each test uses a unique tenant and self-cleans.</summary>
[Collection("admin-db")]
public class BreakGlassIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("ADMIN_TEST_DB");

    private static AdminDbContext Ctx() =>
        new(new DbContextOptionsBuilder<AdminDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static (BreakGlassAdminService, InMemoryAuditOutbox) Build(AdminDbContext db, TimeProvider clock)
    {
        var outbox = new InMemoryAuditOutbox();
        return (new BreakGlassAdminService(db, new AuditClient(outbox, new AuditClientContext("admin-test"), clock), clock), outbox);
    }

    private static ActorContext Actor(string id, string role = "doctor") => new(id, role, "t0", true);

    [Fact]
    public async Task Full_lifecycle_request_dual_approve_step_up_scoped_access_auto_expire()
    {
        if (Db is null) return;
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            var clock = new FixedClock(DateTimeOffset.UtcNow);
            await using var db = Ctx();
            var (svc, outbox) = Build(db, clock);
            var requester = Actor("dr-1");

            var grant = await svc.RequestAsync(requester, tenant, "EMERGENCY_CARE", "unconscious patient",
                ["encounter"], [], 60);
            outbox.Events.Should().Contain(e => e.BreakGlass && e.Severity == AuditSeverity.High && e.DecisionOutcome == "requested");

            // Self-approval is rejected (dual control).
            var self = await svc.ApproveAsync(requester, tenant, grant.GrantId);
            self.Ok.Should().BeFalse();
            self.ReasonCode.Should().Be("self-approval-denied");

            // A distinct approver approves.
            var approve = await svc.ApproveAsync(Actor("dir-1", "medical_director"), tenant, grant.GrantId);
            approve.Ok.Should().BeTrue();

            // Activation requires step-up; without it, denied.
            (await svc.ActivateAsync(requester, tenant, grant.GrantId, stepUpSatisfied: false)).ReasonCode.Should().Be("step-up-required");
            (await svc.ActivateAsync(requester, tenant, grant.GrantId, stepUpSatisfied: true)).Ok.Should().BeTrue();

            // In-scope access is granted + audited high.
            (await svc.RecordAccessAsync(requester, tenant, grant.GrantId, "encounter", "enc-9", "read")).Should().BeTrue();
            // Out-of-scope access is DENIED (no field-deny bypass), still audited.
            (await svc.RecordAccessAsync(requester, tenant, grant.GrantId, "prescription", "rx-9", "read")).Should().BeFalse();
            outbox.Events.Count(e => e.EntityType == "break_glass_access" && e.Severity == AuditSeverity.High).Should().BeGreaterThanOrEqualTo(2);

            // Advance past the window → auto-expire; further access is denied.
            clock.Advance(TimeSpan.FromMinutes(61));
            (await svc.SweepExpiredAsync(tenant)).Should().Be(1);
            (await svc.RecordAccessAsync(requester, tenant, grant.GrantId, "encounter", "enc-9", "read")).Should().BeFalse();
        }
        finally { await Cleanup(tenant); }
    }

    [Fact]
    public async Task Dashboards_are_tenant_scoped_and_audit_their_own_reads()
    {
        if (Db is null) return;
        var mine = "t-" + Guid.NewGuid().ToString("N")[..10];
        var other = "t-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await using var db = Ctx();
            var (svc, _) = Build(db, TimeProvider.System);
            await svc.RequestAsync(Actor("dr-mine"), mine, "EMERGENCY", "reason", ["encounter"], [], 30);
            await svc.RequestAsync(Actor("dr-other"), other, "EMERGENCY", "reason", ["encounter"], [], 30);

            var outbox = new InMemoryAuditOutbox();
            var dash = new DashboardService(db, new AuditClient(outbox, new AuditClientContext("admin-test"), TimeProvider.System));
            var viewer = new ActorContext("admin-1", "org_admin", mine, true);

            var rows = await dash.BreakGlassAsync(viewer, mine);
            rows.Should().HaveCount(1);                          // only my tenant's grant
            rows.Should().OnlyContain(r => r.Requester == "dr-mine");
            outbox.Events.Should().Contain(e => e.EntityType == "dashboard" && e.Action == AuditAction.Read); // read is audited
        }
        finally { await Cleanup(mine); await Cleanup(other); }
    }

    [Fact]
    public async Task Sod_dashboard_surfaces_a_latent_conflict_across_active_bindings()
    {
        if (Db is null) return;
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var subject = "user-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var db = Ctx();
            // Seed a latent conflict directly (bypassing the grant-time SoD guard) to prove the dashboard catches it.
            db.RoleBindings.AddRange(
                Binding(tenant, subject, "doctor", SensitivityTier.T3),
                Binding(tenant, subject, "medical_approval", SensitivityTier.T3));
            await db.SaveChangesAsync();

            var outbox = new InMemoryAuditOutbox();
            var dash = new DashboardService(db, new AuditClient(outbox, new AuditClientContext("admin-test"), TimeProvider.System));
            var rows = await dash.SodViolationsAsync(new ActorContext("admin-1", "org_admin", tenant, true), tenant);

            rows.Should().Contain(r => r.SubjectUserId == subject && r.Reason.Contains("Self-approval"));
        }
        finally { await Cleanup(tenant); }
    }

    private static RoleBinding Binding(string tenant, string subject, string role, SensitivityTier tier) => new()
    {
        BindingId = Guid.NewGuid(), TenantId = tenant, SubjectUserId = subject, Role = role, Tier = tier,
        GrantedBy = "seed", Justification = "test-seed", GrantedAt = DateTimeOffset.UtcNow, Status = BindingStatus.Active,
    };

    private static async Task Cleanup(string tenant)
    {
        if (Db is null) return;
        await using var db = Ctx();
        var grantIds = await db.BreakGlassGrants.Where(g => g.TenantId == tenant).Select(g => g.GrantId).ToListAsync();
        await db.BreakGlassAccesses.Where(a => grantIds.Contains(a.GrantId)).ExecuteDeleteAsync();
        await db.BreakGlassGrants.Where(g => g.TenantId == tenant).ExecuteDeleteAsync();
        await db.RoleBindings.Where(b => b.TenantId == tenant).ExecuteDeleteAsync();
    }
}
