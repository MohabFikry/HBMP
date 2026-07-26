using FluentAssertions;
using Mersal.Admin.Api;
using Mersal.Admin.Domain;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Tests;

/// <summary>Governance persistence at the datastore (env-gated <c>ADMIN_TEST_DB</c>, live PG). Proves the phase-8b.2
/// acceptance criteria: a master-data edit APPENDS an effective-dated version and a historical date still resolves
/// the OLD version (FR-MDM-007); a linted template save is rejected for PHI-in-SMS and audited; and a config change
/// is typed + versioned. Each test uses a unique code/key and self-cleans.</summary>
[Collection("admin-db")]
public class GovernanceIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("ADMIN_TEST_DB");

    private static AdminDbContext Ctx() =>
        new(new DbContextOptionsBuilder<AdminDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static (GovernanceService, InMemoryAuditOutbox) Build(AdminDbContext db, TimeProvider clock)
    {
        var outbox = new InMemoryAuditOutbox();
        return (new GovernanceService(db, new AuditClient(outbox, new AuditClientContext("admin-test"), clock), clock), outbox);
    }

    private static readonly ActorContext Gov = new("gov-1", "medical_director", "t0", Mfa: true);

    [SkippableFact]
    public async Task A_master_data_edit_appends_a_version_and_history_resolves_the_old_one()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var code = "TST" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            await using var db = Ctx();
            var (svc, _) = Build(db, clock);

            // v1 in force from 2026-01-01.
            var v1 = await svc.UpsertMasterDataAsync(Gov, CodeSystem.Icd10, code, new { title = "Old title", billable = true }, "initial load");
            v1.VersionNo.Should().Be(1);

            // v2 effective 2026-06-01 (title corrected).
            clock.Advance(TimeSpan.FromDays(151)); // → 2026-06-01
            var v2 = await svc.UpsertMasterDataAsync(Gov, CodeSystem.Icd10, code, new { title = "New title", billable = true }, "title correction");
            v2.VersionNo.Should().Be(2);

            // A record dated in the v1 window resolves the OLD version; a current date resolves the new one.
            var historical = await svc.ResolveAsOfAsync(CodeSystem.Icd10, code, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
            historical!.VersionNo.Should().Be(1);
            historical.AttributesJson.Should().Contain("Old title");

            var current = await svc.ResolveAsOfAsync(CodeSystem.Icd10, code, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
            current!.VersionNo.Should().Be(2);
            current.AttributesJson.Should().Contain("New title");
        }
        finally { await CleanupMasterData(code); }
    }

    [SkippableFact]
    public async Task A_template_with_phi_in_an_sms_body_is_rejected_and_audited()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var key = "tpl-" + Guid.NewGuid().ToString("N")[..8];
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await using var db = Ctx();
            var (svc, outbox) = Build(db, TimeProvider.System);
            var actor = new ActorContext("gov-1", "medical_director", tenant, true);

            var bad = await svc.SaveTemplateAsync(actor, tenant, key, "sms", "", "",
                "Your diagnosis {diagnosis} is ready", "تشخيصك {diagnosis} جاهز");
            bad.Ok.Should().BeFalse();
            bad.Errors.Should().Contain(e => e.Contains("diagnosis"));
            outbox.Events.Should().Contain(e => e.DecisionOutcome == "rejected" && e.DecisionReasonCode == "template-lint");

            // A clean PHI-free bilingual template saves as v1.
            var ok = await svc.SaveTemplateAsync(actor, tenant, key, "sms", "", "",
                "Your appointment on {date} is confirmed", "تم تأكيد موعدك في {date}");
            ok.Ok.Should().BeTrue();
            ok.Version!.VersionNo.Should().Be(1);
        }
        finally { await CleanupTemplates(tenant); }
    }

    [SkippableFact]
    public async Task A_config_change_is_typed_and_versioned()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var key = "approval.high_cost_threshold";
        try
        {
            await using var db = Ctx();
            var (svc, _) = Build(db, TimeProvider.System);
            var actor = new ActorContext("admin-1", "org_admin", tenant, true);

            var bad = await svc.SetConfigAsync(actor, tenant, key, ConfigValueType.Whole, "not-a-number");
            bad.Ok.Should().BeFalse();
            bad.Error.Should().Be("not-an-integer");

            var v1 = await svc.SetConfigAsync(actor, tenant, key, ConfigValueType.Whole, "5000");
            v1.Ok.Should().BeTrue();
            v1.Config!.VersionNo.Should().Be(1);

            var v2 = await svc.SetConfigAsync(actor, tenant, key, ConfigValueType.Whole, "7500");
            v2.Config!.VersionNo.Should().Be(2);

            // Only one currently-in-force row (the prior was closed).
            (await db.SystemConfigs.CountAsync(c => c.TenantId == tenant && c.Key == key && c.EffectiveTo == null))
                .Should().Be(1);
        }
        finally { await CleanupConfig(tenant); }
    }

    private static async Task CleanupMasterData(string code)
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = Ctx();
        await db.MasterDataVersions.Where(v => v.Code == code).ExecuteDeleteAsync();
    }

    private static async Task CleanupTemplates(string tenant)
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = Ctx();
        await db.TemplateVersions.Where(t => t.TenantId == tenant).ExecuteDeleteAsync();
    }

    private static async Task CleanupConfig(string tenant)
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = Ctx();
        await db.SystemConfigs.Where(c => c.TenantId == tenant).ExecuteDeleteAsync();
    }
}
