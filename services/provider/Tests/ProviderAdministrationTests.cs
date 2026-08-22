using FluentAssertions;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProviderEntity = Mersal.Provider.Domain.Provider;

namespace Mersal.Provider.Tests;

/// <summary>
/// Phase 19.9 — the datastore half of provider administration (design 58).
///
/// <para>These assert the guarantees that survive an endpoint being bypassed: what the triggers record, that
/// the history twins are tenant-isolated, and that the one-primary-location rule is the database's rather
/// than the application's. Env-gated on <c>PROVIDER_TEST_DB_OWNER</c> (and <c>PROVIDER_TEST_DB_APP</c> for
/// the isolation proof, which MUST run as the NOBYPASSRLS role or it would falsely pass).</para>
/// </summary>
public class ProviderAdministrationTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("PROVIDER_TEST_DB_OWNER");
    private static readonly string? App = Environment.GetEnvironmentVariable("PROVIDER_TEST_DB_APP");

    private static ProviderDbContext Ctx() =>
        new(new DbContextOptionsBuilder<ProviderDbContext>().UseNpgsql(Owner).UseSnakeCaseNamingConvention().Options);

    private static string T() => "t-" + Guid.NewGuid().ToString("N")[..10];

    private static ProviderEntity NewProvider(string tenant, string name = "Test Provider")
    {
        var now = DateTimeOffset.UtcNow;
        return new ProviderEntity
        {
            ProviderId = Guid.NewGuid(), TenantId = tenant,
            ProviderCode = "PC-" + Guid.NewGuid().ToString("N")[..8], LegalName = name,
            ProviderType = ProviderType.Clinic, Status = ProviderStatus.Suspended,
            OnboardingState = OnboardingState.Draft, CreatedAt = now, UpdatedAt = now,
            CreatedBy = "user-1", CreatedByName = "Amal Fahmy",
        };
    }

    // ── The provider twin ───────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Every_provider_write_is_snapshotted_with_its_tenant_and_its_actor()
    {
        Skip.If(Owner is null, "PROVIDER_TEST_DB_OWNER not set — DB integration test skipped.");
        var tenant = T();
        var p = NewProvider(tenant);
        try
        {
            await using (var db = Ctx())
            {
                db.Providers.Add(p);
                await db.SaveChangesAsync();
            }

            await using (var db = Ctx())
            {
                var live = await db.Providers.FirstAsync(x => x.ProviderId == p.ProviderId);
                live.Status = ProviderStatus.Suspended;
                live.StatusReason = "licence lapsed while we wait for the renewal";
                live.StatusActor = "user-2";
                live.StatusActorName = "Hana Said";
                live.UpdatedBy = "user-2";
                live.UpdatedByName = "Hana Said";
                await db.SaveChangesAsync();
            }

            await using var read = Ctx();
            var entries = await read.ProviderHistory.AsNoTracking()
                .Where(h => h.ProviderId == p.ProviderId).OrderBy(h => h.HistoryId).ToListAsync();

            entries.Should().HaveCount(2, "the trigger records the insert and the update");
            entries[0].Operation.Should().Be("INSERT");
            entries[1].Operation.Should().Be("UPDATE");

            // 0001 created this table with no tenant column at all, so nothing about a snapshot said whose
            // it was. 0015 backfills it from the snapshot and makes it NOT NULL.
            entries.Should().OnlyContain(e => e.TenantId == tenant);

            entries[1].RowSnapshot.Should().Contain("licence lapsed while we wait for the renewal",
                "the reason is the whole point of the twin: the audit chain records that it happened and is " +
                "read by Compliance, not by the team who has to decide whether to switch them back on");
            entries[1].RowSnapshot.Should().Contain("Hana Said");
        }
        finally { await Cleanup(tenant); }
    }

    // ── Isolation: the gap 0015 closes ──────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task One_tenants_provider_history_is_invisible_to_another()
    {
        // The whole point of this one: `provider.provider_history` had NO row-level security from 0001 until
        // 0015 — no tenant column to filter on and no policy that could use one. Nothing leaked only because
        // nothing had ever read it, and 19.9 adds the read. Must run as the NOBYPASSRLS app role: a
        // superuser or a BYPASSRLS role would pass this while the table stayed open.
        Skip.If(Owner is null || App is null, "test DB not configured — set the *_TEST_DB env vars to run this.");
        var mine = T();
        var theirs = T();
        var a = NewProvider(mine, "Mine");
        var b = NewProvider(theirs, "Theirs");
        try
        {
            await using (var db = Ctx())
            {
                db.Providers.AddRange(a, b);
                await db.SaveChangesAsync();
            }

            (await HistoryCountUnderTenant(mine, a.ProviderId)).Should().Be(1);
            (await HistoryCountUnderTenant(mine, b.ProviderId)).Should().Be(0,
                "another tenant's provider history must not be readable, and the policy is fail-closed");
            (await HistoryCountUnderTenant("", a.ProviderId)).Should().Be(0,
                "an unset tenant GUC matches nothing rather than everything");
        }
        finally { await Cleanup(mine); await Cleanup(theirs); }
    }

    private static async Task<int> HistoryCountUnderTenant(string tenant, Guid providerId)
    {
        await using var conn = new NpgsqlConnection(App);
        await conn.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false), set_config('app.provider_id', '', false)", conn))
        {
            set.Parameters.AddWithValue("t", tenant);
            await set.ExecuteNonQueryAsync();
        }
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM provider.provider_history WHERE provider_id = @p", conn);
        cmd.Parameters.AddWithValue("p", providerId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    // ── The location twin, and the rule that made "make primary" impossible ─────────────────────────────

    [SkippableFact]
    public async Task Two_primary_locations_are_refused_by_the_database()
    {
        // This is why promotion has to demote FIRST and in one transaction. The index has enforced it since
        // 0001, which is also why there was no way to move the primary at all: adding a second answered 409
        // and no demote existed.
        Skip.If(Owner is null, "PROVIDER_TEST_DB_OWNER not set — DB integration test skipped.");
        var tenant = T();
        var p = NewProvider(tenant);
        try
        {
            await using var db = Ctx();
            db.Providers.Add(p);
            db.Locations.Add(Location(tenant, p.ProviderId, "Head office", primary: true));
            await db.SaveChangesAsync();

            db.Locations.Add(Location(tenant, p.ProviderId, "Second site", primary: true));
            var save = async () => await db.SaveChangesAsync();

            (await save.Should().ThrowAsync<DbUpdateException>())
                .Which.InnerException.Should().BeOfType<PostgresException>()
                .Which.ConstraintName.Should().Be("uq_location_primary");
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Moving_the_primary_location_is_recorded_on_both_rows()
    {
        Skip.If(Owner is null, "PROVIDER_TEST_DB_OWNER not set — DB integration test skipped.");
        var tenant = T();
        var p = NewProvider(tenant);
        var first = Location(tenant, p.ProviderId, "Head office", primary: true);
        var second = Location(tenant, p.ProviderId, "New head office", primary: false);
        try
        {
            await using (var db = Ctx())
            {
                db.Providers.Add(p);
                db.Locations.AddRange(first, second);
                await db.SaveChangesAsync();
            }

            // Demote, then promote — the order the endpoint uses, and the only one the index allows.
            await using (var db = Ctx())
            {
                var demote = await db.Locations.FirstAsync(l => l.LocationId == first.LocationId);
                demote.IsPrimary = false;
                demote.UpdatedByName = "Hana Said";
                await db.SaveChangesAsync();

                var promote = await db.Locations.FirstAsync(l => l.LocationId == second.LocationId);
                promote.IsPrimary = true;
                promote.UpdatedByName = "Hana Said";
                await db.SaveChangesAsync();
            }

            await using var read = Ctx();
            var moved = await read.LocationHistory.AsNoTracking()
                .Where(h => h.LocationId == second.LocationId).OrderBy(h => h.HistoryId).ToListAsync();

            moved.Should().HaveCount(2);
            moved.Should().OnlyContain(h => h.TenantId == tenant);
            moved[0].RowSnapshot.Should().Contain("\"is_primary\": false");
            moved[1].RowSnapshot.Should().Contain("\"is_primary\": true",
                "a provider-level snapshot cannot record this — moving the primary never touches the " +
                "provider row, and it is the address referrals are sent to");
        }
        finally { await Cleanup(tenant); }
    }

    // ── The contract twin ───────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Terminating_a_contract_records_why_on_its_own_timeline()
    {
        Skip.If(Owner is null, "PROVIDER_TEST_DB_OWNER not set — DB integration test skipped.");
        var tenant = T();
        var p = NewProvider(tenant);
        var c = new ProviderContract
        {
            ContractId = Guid.NewGuid(), ProviderId = p.ProviderId, TenantId = tenant,
            ContractNo = "CN-" + Guid.NewGuid().ToString("N")[..8],
            EffectiveFrom = new DateOnly(2026, 1, 1), Status = ContractStatus.Active,
        };
        try
        {
            await using (var db = Ctx())
            {
                db.Providers.Add(p);
                db.Contracts.Add(c);
                await db.SaveChangesAsync();
            }

            await using (var db = Ctx())
            {
                var live = await db.Contracts.FirstAsync(x => x.ContractId == c.ContractId);
                live.Status = ContractStatus.Terminated;
                live.StatusReason = "the centre closed and gave us sixty days' notice";
                live.StatusActorName = "Hana Said";
                live.StatusChangedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }

            await using var read = Ctx();
            var entries = await read.ContractHistory.AsNoTracking()
                .Where(h => h.ContractId == c.ContractId).OrderByDescending(h => h.HistoryId).ToListAsync();

            entries.Should().HaveCount(2);
            entries[0].RowSnapshot.Should().Contain("the centre closed and gave us sixty days' notice");
            entries[0].TenantId.Should().Be(tenant);
        }
        finally { await Cleanup(tenant); }
    }

    // ── Readiness reads the guard rather than restating it ──────────────────────────────────────────────

    [Fact]
    public void The_readiness_checklist_agrees_with_the_activation_guard()
    {
        // The endpoint returns all four conditions AND the guard's verdict. If they could disagree, the
        // screen would show a complete checklist beside a refusal — which is worse than the 422 it replaced.
        var complete = new OnboardingWorkflow.Readiness(true, true, true, true);
        OnboardingWorkflow.GuardActivation(complete).Allowed.Should().BeTrue();

        foreach (var missing in new[]
        {
            new OnboardingWorkflow.Readiness(false, true, true, true),
            new OnboardingWorkflow.Readiness(true, false, true, true),
            new OnboardingWorkflow.Readiness(true, true, false, true),
            new OnboardingWorkflow.Readiness(true, true, true, false),
        })
        {
            var verdict = OnboardingWorkflow.GuardActivation(missing);
            verdict.Allowed.Should().BeFalse();
            verdict.Reason.Should().NotBeNullOrWhiteSpace(
                "the screen renders the server's own sentence rather than composing one of its own");
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────

    private static ProviderLocation Location(string tenant, Guid providerId, string name, bool primary) => new()
    {
        LocationId = Guid.NewGuid(), ProviderId = providerId, TenantId = tenant,
        Name = name, IsPrimary = primary,
    };

    private static async Task Cleanup(string tenant)
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "DELETE FROM provider.provider_contract_history WHERE tenant_id = @t",
            "DELETE FROM provider.provider_location_history WHERE tenant_id = @t",
            "DELETE FROM provider.provider_history WHERE tenant_id = @t",
            "DELETE FROM provider.contract_service_line WHERE tenant_id = @t",
            "DELETE FROM provider.provider_contract WHERE tenant_id = @t",
            "DELETE FROM provider.provider_location WHERE tenant_id = @t",
            "DELETE FROM provider.provider_credential WHERE tenant_id = @t",
            "DELETE FROM provider.provider_user WHERE tenant_id = @t",
            "DELETE FROM provider.provider WHERE tenant_id = @t",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("t", tenant);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
