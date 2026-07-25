using FluentAssertions;
using Mersal.Admin.Api;
using Mersal.Admin.Domain;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Tests;

/// <summary>Admin lifecycle at the datastore (env-gated <c>ADMIN_TEST_DB</c>, live PG). Proves the phase-8b.1
/// acceptance criteria end to end: an SoD-incompatible grant is rejected and audited; a de-provisioned user has NO
/// effective roles (access denied everywhere immediately); an access-review campaign snapshots T3/T4 grants and an
/// unconfirmed grant AUTO-EXPIRES at the deadline (revoking the binding); and every admin write is audited. Each
/// test scopes to a unique tenant and self-cleans.</summary>
[Collection("admin-db")]
public class AdminIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("ADMIN_TEST_DB");

    private static AdminDbContext Ctx() =>
        new(new DbContextOptionsBuilder<AdminDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static (RoleAdminService, AccessReviewService, InMemoryAuditOutbox) Build(AdminDbContext db, TimeProvider clock)
    {
        var outbox = new InMemoryAuditOutbox();
        var audit = new AuditClient(outbox, new AuditClientContext("admin-test"), clock);
        return (new RoleAdminService(db, audit, clock), new AccessReviewService(db, audit, clock), outbox);
    }

    private static readonly ActorContext Admin = new("admin-1", "org_admin", null, Mfa: true);

    [Fact]
    public async Task An_sod_incompatible_grant_is_rejected_and_audited()
    {
        if (Db is null) return;
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var subject = "user-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var db = Ctx();
            var (svc, _, outbox) = Build(db, TimeProvider.System);

            var first = await svc.GrantAsync(Admin, tenant, subject, "doctor", ScopeType.Tenant, null, "treating physician");
            first.Ok.Should().BeTrue();

            // Now try to also make them a medical approver — self-approval SoD conflict.
            var conflict = await svc.GrantAsync(Admin, tenant, subject, "medical_approval", ScopeType.Tenant, null, "utilization review");
            conflict.Ok.Should().BeFalse();
            conflict.ReasonCode.Should().Be("sod-conflict");
            conflict.Violations.Should().NotBeEmpty();
            outbox.Events.Should().Contain(e =>
                e.Action == AuditAction.Grant && e.DecisionOutcome == "denied" && e.Severity == AuditSeverity.High);

            (await svc.EffectiveRolesAsync(tenant, subject)).Should().ContainSingle().Which.Should().Be("doctor");
        }
        finally { await Cleanup(tenant); }
    }

    [Fact]
    public async Task A_deprovisioned_user_has_no_effective_roles_denied_everywhere()
    {
        if (Db is null) return;
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var subject = "user-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var db = Ctx();
            var (svc, _, outbox) = Build(db, TimeProvider.System);

            await svc.GrantAsync(Admin, tenant, subject, "reception", ScopeType.Tenant, null, "front desk");
            await svc.GrantAsync(Admin, tenant, subject, "call_center", ScopeType.Tenant, null, "phone support");
            (await svc.EffectiveRolesAsync(tenant, subject)).Should().HaveCount(2);

            await svc.DeprovisionAsync(Admin, tenant, subject, "left the organization");

            // No effective roles anywhere; the block is recorded and audited high-severity.
            (await svc.EffectiveRolesAsync(tenant, subject)).Should().BeEmpty();
            (await db.RoleBindings.CountAsync(b => b.TenantId == tenant && b.SubjectUserId == subject && b.Status == BindingStatus.Active))
                .Should().Be(0);
            outbox.Events.Should().Contain(e =>
                e.EntityType == "user" && e.Action == AuditAction.StateChange && e.Severity == AuditSeverity.High);
        }
        finally { await Cleanup(tenant); }
    }

    [Fact]
    public async Task An_unrecertified_grant_auto_expires_at_the_review_deadline()
    {
        if (Db is null) return;
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var keep = "user-" + Guid.NewGuid().ToString("N")[..8];
        var drop = "user-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var clock = new FixedClock(DateTimeOffset.UtcNow);
            await using var db = Ctx();
            var (svc, review, outbox) = Build(db, clock);

            // Two T3 grants (doctor). A low-tier reception grant must NOT be pulled into a T3 campaign.
            await svc.GrantAsync(Admin, tenant, keep, "doctor", ScopeType.Tenant, null, "treating");
            await svc.GrantAsync(Admin, tenant, drop, "doctor", ScopeType.Tenant, null, "treating");
            await svc.GrantAsync(Admin, tenant, keep, "reception", ScopeType.Tenant, null, "desk");

            var campaign = await review.CreateCampaignAsync(Admin, tenant, "Q3 review", SensitivityTier.T3, clock.GetUtcNow().AddDays(14));
            campaign.Items.Should().HaveCount(2); // only the two T3 doctor grants

            // Recertify one; leave the other untouched.
            var keepItem = campaign.Items.Single(i => i.SubjectUserId == keep);
            var dropItem = campaign.Items.Single(i => i.SubjectUserId == drop);
            (await review.RecertifyAsync(Admin, tenant, keepItem.ItemId, "still treating")).Should().BeTrue();

            // Before the deadline the sweep does nothing.
            (await review.SweepExpiredAsync(Admin, tenant, campaign.CampaignId)).Should().Be(0);

            // Advance past the deadline and sweep: the untouched grant auto-expires (binding revoked).
            clock.Advance(TimeSpan.FromDays(15));
            (await review.SweepExpiredAsync(Admin, tenant, campaign.CampaignId)).Should().Be(1);

            (await svc.EffectiveRolesAsync(tenant, drop)).Should().BeEmpty();             // dropped
            (await svc.EffectiveRolesAsync(tenant, keep)).Should().Contain("doctor");     // kept
            var reloaded = await review.CampaignStatus(db, campaign.CampaignId);
            reloaded.Should().Be(CampaignStatus.Closed);
            outbox.Events.Should().Contain(e =>
                e.EntityType == "access_review_item" && e.DecisionOutcome == "AutoExpired");
        }
        finally { await Cleanup(tenant); }
    }

    private static async Task Cleanup(string tenant)
    {
        if (Db is null) return;
        await using var db = Ctx();
        // FK order: items → campaigns; bindings/deprovision are independent. Test-only scoped teardown.
        var campaignIds = await db.Campaigns.Where(c => c.TenantId == tenant).Select(c => c.CampaignId).ToListAsync();
        await db.ReviewItems.Where(i => campaignIds.Contains(i.CampaignId)).ExecuteDeleteAsync();
        await db.Campaigns.Where(c => c.TenantId == tenant).ExecuteDeleteAsync();
        await db.RoleBindings.Where(b => b.TenantId == tenant).ExecuteDeleteAsync();
        await db.DeprovisionedUsers.Where(d => d.TenantId == tenant).ExecuteDeleteAsync();
    }
}

/// <summary>A mutable clock for the access-review deadline test.</summary>
public sealed class FixedClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Small test helper to read back a campaign's status.</summary>
public static class AccessReviewTestExtensions
{
    public static async Task<CampaignStatus> CampaignStatus(this AccessReviewService _, AdminDbContext db, Guid campaignId)
    {
        var c = await db.Campaigns.AsNoTracking().SingleAsync(x => x.CampaignId == campaignId);
        return c.Status;
    }
}
