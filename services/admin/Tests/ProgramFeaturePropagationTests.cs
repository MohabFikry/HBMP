using System.Text.Json;
using FluentAssertions;
using Mersal.Admin.Api;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;
using Mersal.Authz;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mersal.Admin.Tests;

/// <summary>
/// 21.4 propagation, admin side — flipping a switch must PUBLISH, not only persist.
///
/// The switches are administered here but enforced wherever the module lives, off the `features` claim, so a
/// change that is written and not published produces the worst available outcome: a tenant whose administration
/// screen says "enabled" while every token issued says otherwise, with nothing reporting a failure. The event is
/// staged through the outbox in the same transaction as the row and its history, so "recorded" and "will
/// propagate" are the same fact.
///
/// Env-gated on ADMIN_TEST_DB. DB-less CI skips.
/// </summary>
[Collection("admin-db")]
public class ProgramFeaturePropagationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("ADMIN_TEST_DB");

    private static AdminDbContext Ctx() =>
        new(new DbContextOptionsBuilder<AdminDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static readonly ActorContext Actor = new("admin-1", "super_admin", "t-1", Mfa: true);

    private static string NewTenant() => "t-test-" + Guid.NewGuid().ToString("N")[..12];

    private static (ProgramAdminService svc, InMemoryOutbox outbox) Service(AdminDbContext db)
    {
        var outbox = new InMemoryOutbox();
        var audit = new AuditClient(new InMemoryAuditOutbox(), new AuditClientContext("admin-service"), TimeProvider.System);
        return (new ProgramAdminService(db, audit, TimeProvider.System, outbox), outbox);
    }

    [SkippableFact]
    public async Task Switching_a_feature_publishes_the_change_for_the_issuer_to_project()
    {
        Skip.If(Db is null, "ADMIN_TEST_DB not set — DB integration test skipped.");
        await using var db = Ctx();
        var (svc, outbox) = Service(db);
        var tenant = NewTenant();

        try
        {
            await svc.SetFeatureAsync(Actor, tenant, ProgramFeatures.Claims, enabled: true, "onboarded onto claims");

            var staged = await outbox.DequeueBatchAsync(10);
            var msg = staged.Should().ContainSingle().Subject;
            msg.EventType.Should().Be("TenantFeatureChanged");
            msg.Destination.Should().Be("admin.events", "identity-service tails this queue");

            using var doc = JsonDocument.Parse(msg.Payload);
            var root = doc.RootElement;
            // tenantId is what the consumer's envelope reader uses to attribute the change; without it the
            // event is unattributable and gets dead-lettered.
            root.GetProperty("tenantId").GetString().Should().Be(tenant);
            root.GetProperty("featureKey").GetString().Should().Be(ProgramFeatures.Claims);
            root.GetProperty("enabled").GetBoolean().Should().BeTrue();
            // changedAt is the ordering guard's input: the projection compares it and refuses to move backwards,
            // so an event without it cannot be applied safely.
            root.GetProperty("changedAt").TryGetDateTimeOffset(out _).Should().BeTrue();
        }
        finally
        {
            await CleanupAsync(db, tenant);
        }
    }

    /// <summary>Switching OFF propagates exactly as switching on does. Publishing only the enables would leave
    /// every token asserting a module the administrator has withdrawn.</summary>
    [SkippableFact]
    public async Task Switching_a_feature_off_also_publishes()
    {
        Skip.If(Db is null, "ADMIN_TEST_DB not set — DB integration test skipped.");
        await using var db = Ctx();
        var (svc, outbox) = Service(db);
        var tenant = NewTenant();

        try
        {
            await svc.SetFeatureAsync(Actor, tenant, ProgramFeatures.Interop, enabled: true, "on");
            await svc.SetFeatureAsync(Actor, tenant, ProgramFeatures.Interop, enabled: false, "withdrawn");

            var staged = await outbox.DequeueBatchAsync(10);
            staged.Should().HaveCount(2);
            JsonDocument.Parse(staged[^1].Payload).RootElement
                .GetProperty("enabled").GetBoolean().Should().BeFalse();
        }
        finally
        {
            await CleanupAsync(db, tenant);
        }
    }

    /// <summary>The switch is still persisted and still audited — the event is an addition to that record, not a
    /// replacement for it. A consumer that never runs must not cost us the administrative history.</summary>
    [SkippableFact]
    public async Task The_row_and_its_history_are_written_alongside_the_event()
    {
        Skip.If(Db is null, "ADMIN_TEST_DB not set — DB integration test skipped.");
        await using var db = Ctx();
        var (svc, _) = Service(db);
        var tenant = NewTenant();

        try
        {
            await svc.SetFeatureAsync(Actor, tenant, ProgramFeatures.Pharmacy, enabled: true, "onboarded");

            var enabled = await db.Database.SqlQueryRaw<bool>(
                """
                SELECT enabled AS "Value" FROM admin.tenant_feature
                WHERE tenant_id = {0} AND feature_key = {1}
                """, tenant, ProgramFeatures.Pharmacy).SingleAsync();
            enabled.Should().BeTrue();

            var history = await db.Database.SqlQueryRaw<int>(
                """
                SELECT count(*)::int AS "Value" FROM admin.tenant_feature_history
                WHERE tenant_id = {0} AND feature_key = {1}
                """, tenant, ProgramFeatures.Pharmacy).SingleAsync();
            history.Should().Be(1);
        }
        finally
        {
            await CleanupAsync(db, tenant);
        }
    }

    private static async Task CleanupAsync(AdminDbContext db, string tenant)
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM admin.tenant_feature WHERE tenant_id = {0}", [tenant]);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM admin.tenant_feature_history WHERE tenant_id = {0}", [tenant]);
    }
}
