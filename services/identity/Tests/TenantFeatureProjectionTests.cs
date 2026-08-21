using FluentAssertions;
using Mersal.Authz;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mersal.Identity.Tests;

/// <summary>
/// 21.4 propagation — the projection itself, against a real Postgres.
///
/// The behaviour under test is what happens when the broker misbehaves, because that is the only thing that can
/// corrupt this table. Delivery is at-least-once and unordered: the same event arrives twice, and an older event
/// can arrive after a newer one. Neither may leave the issuer minting tokens from a state a platform
/// administrator did not set — and the failure mode is silent, since a stale projection looks exactly like a
/// correct one.
///
/// Env-gated on IDENTITY_TEST_DB. DB-less CI skips.
/// </summary>
public class TenantFeatureProjectionTests
{
    private static IdentityStoreDbContext Ctx() => IdentityTestDb.NewContext();

    /// <summary>A tenant id unique to each test, so runs never contend and nothing depends on the backfill.</summary>
    private static string NewTenant() => "t-test-" + Guid.NewGuid().ToString("N")[..12];

    private static async Task CleanupAsync(IdentityStoreDbContext db, string tenant) =>
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM identity.tenant_feature WHERE tenant_id = {0}", [tenant]);

    [SkippableFact]
    public async Task Enabled_features_are_read_back_for_the_tenant_that_owns_them()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = Ctx();
        var store = new TenantFeatureStore(db);
        var tenant = NewTenant();
        var other = NewTenant();
        var t0 = DateTimeOffset.UtcNow;

        try
        {
            await store.ApplyAsync(tenant, ProgramFeatures.CallCentre, true, t0, Guid.NewGuid());
            await store.ApplyAsync(tenant, ProgramFeatures.Emr, true, t0, Guid.NewGuid());
            // Explicitly OFF is not the same as absent in the table, but IS the same in the answer: both mean
            // "not enabled", and returning it would put a disabled feature in a token.
            await store.ApplyAsync(tenant, ProgramFeatures.Claims, false, t0, Guid.NewGuid());
            // Another organisation's switch must never reach this tenant's token.
            await store.ApplyAsync(other, ProgramFeatures.Interop, true, t0, Guid.NewGuid());

            var enabled = await store.EnabledForAsync(tenant);

            enabled.Should().BeEquivalentTo([ProgramFeatures.CallCentre, ProgramFeatures.Emr]);
            enabled.Should().NotContain(ProgramFeatures.Interop, "that switch belongs to another tenant");
            enabled.Should().BeInAscendingOrder("a stable claim keeps two tokens for one principal comparable");
        }
        finally
        {
            await CleanupAsync(db, tenant);
            await CleanupAsync(db, other);
        }
    }

    [SkippableFact]
    public async Task An_unknown_tenant_has_nothing_enabled()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = Ctx();

        (await new TenantFeatureStore(db).EnabledForAsync(NewTenant())).Should().BeEmpty();
    }

    /// <summary>
    /// THE defect this design exists to prevent. An administrator switches Claims off, then back on. The broker
    /// redelivers the "off" — which it is entitled to do — after the "on". Without the changed_at guard the
    /// projection takes it, and from that moment every token says Claims is disabled for an organisation whose
    /// own administration screen says it is enabled. Nothing errors; the module is simply dark.
    /// </summary>
    [SkippableFact]
    public async Task An_out_of_order_redelivery_cannot_move_a_switch_backwards()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = Ctx();
        var store = new TenantFeatureStore(db);
        var tenant = NewTenant();
        var switchedOff = DateTimeOffset.UtcNow.AddMinutes(-5);
        var switchedOn = DateTimeOffset.UtcNow;

        try
        {
            await store.ApplyAsync(tenant, ProgramFeatures.Claims, false, switchedOff, Guid.NewGuid());
            (await store.ApplyAsync(tenant, ProgramFeatures.Claims, true, switchedOn, Guid.NewGuid()))
                .Should().BeTrue("the newer change applies");

            // The stale "off" arrives late.
            var applied = await store.ApplyAsync(tenant, ProgramFeatures.Claims, false, switchedOff, Guid.NewGuid());

            applied.Should().BeFalse("an older state must be refused, not written");
            (await store.EnabledForAsync(tenant)).Should().Contain(ProgramFeatures.Claims);
        }
        finally
        {
            await CleanupAsync(db, tenant);
        }
    }

    /// <summary>A genuine re-send of the NEWEST change is still applied: it writes the same value, so it costs
    /// nothing, and refusing it would strand a first delivery that died after claiming its event id.</summary>
    [SkippableFact]
    public async Task A_redelivery_of_the_current_state_is_applied_not_refused()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = Ctx();
        var store = new TenantFeatureStore(db);
        var tenant = NewTenant();
        var at = DateTimeOffset.UtcNow;

        try
        {
            await store.ApplyAsync(tenant, ProgramFeatures.Pharmacy, true, at, Guid.NewGuid());
            (await store.ApplyAsync(tenant, ProgramFeatures.Pharmacy, true, at, Guid.NewGuid())).Should().BeTrue();
            (await store.EnabledForAsync(tenant)).Should().BeEquivalentTo([ProgramFeatures.Pharmacy]);
        }
        finally
        {
            await CleanupAsync(db, tenant);
        }
    }

    /// <summary>
    /// Dedupe has to be durable and atomic. Durable because the question is "have I EVER seen this id", which a
    /// process lifetime cannot answer — an in-memory set re-applies everything after a restart. Atomic because
    /// two consumers racing one redelivery must produce exactly one claim; a SELECT-then-INSERT lets both
    /// through and the handler runs twice.
    /// </summary>
    [SkippableFact]
    public async Task The_dedupe_claim_succeeds_once_and_survives_a_new_context()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        var eventId = Guid.NewGuid();

        try
        {
            await using (var db = Ctx())
            {
                var store = new DbProcessedEventStore(db);
                (await store.TryBeginAsync(eventId)).Should().BeTrue("first sight of the id");
                (await store.TryBeginAsync(eventId)).Should().BeFalse("redelivery is a no-op");
            }

            // A different context stands in for a restarted consumer.
            await using (var fresh = Ctx())
            {
                (await new DbProcessedEventStore(fresh).TryBeginAsync(eventId))
                    .Should().BeFalse("the record outlives the process that made it");
            }
        }
        finally
        {
            await using var db = Ctx();
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM identity.processed_event WHERE event_id = {0}", [eventId]);
        }
    }

    /// <summary>
    /// The backfill's promise: a tenant that already existed when the feature gate shipped has every module
    /// ON, so wiring the gate changes nothing for them. Get this wrong and deploying the gate takes live
    /// partner organisations off modules they are using today.
    /// </summary>
    /// <remarks>
    /// <para><b>This test used to skip itself.</b> It read the tenants out of
    /// <c>identity.tenant_membership</c> and answered <c>Skip.If(tenants.Count == 0, "no memberships in this
    /// database")</c> — so it ran on a developer's seeded database and skipped in CI, where the membership
    /// table is empty. A skip reports green having proven nothing, which is precisely what
    /// <c>check-skipped-tests.py</c> exists to refuse, and this one had been failing that gate silently
    /// behind a louder test failure.</para>
    ///
    /// <para><b>What it tests now.</b> Two claims, and only the second one needed data that might not be
    /// there. The first — that migration 0015's backfill turns on all eleven modules for a tenant that has a
    /// membership — is a property of the SQL, and it runs anywhere: seed a membership, execute the backfill
    /// block <i>read out of the migration file itself</i>, assert. The SQL is not copied here, for the reason
    /// <see cref="MembershipBackfillParityTests"/> gives about 0010: a test that restates a migration proves
    /// the restatement.</para>
    ///
    /// <para>The second claim — that the tenants in THIS database all have their eleven — is then made over
    /// whatever is present, and is simply vacuous when nothing is. That is the right shape: a deployment
    /// check that finds nothing to check has not failed, and it is no longer standing in for the engineering
    /// claim above.</para>
    /// </remarks>
    [SkippableFact]
    public async Task Existing_tenants_were_backfilled_with_the_whole_catalogue_enabled()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = Ctx();
        var store = new TenantFeatureStore(db);
        var seeded = NewTenant();

        var userId = Guid.NewGuid();
        try
        {
            // ---- The claim about the MIGRATION, which needs no pre-existing data ----------------------
            // A membership needs a real user: `tenant_membership.user_id` is a foreign key, so a random uuid
            // is refused (23503). Same shape as MembershipBackfillParityTests.SeedUser.
            var uname = $"tf-{userId:N}";
            db.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = uname,
                NormalizedUserName = uname.ToUpperInvariant(),
                TenantId = seeded,
                DisplayName = "Tenant feature backfill",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                SecurityStamp = Guid.NewGuid().ToString(),
                ConcurrencyStamp = Guid.NewGuid().ToString(),
            });
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO identity.tenant_membership (membership_id, tenant_id, user_id, status, created_by)
                VALUES (gen_random_uuid(), {0}, {1}, 'Active', 'test');
                """,
                [seeded, userId]);

            await db.Database.ExecuteSqlRawAsync(BackfillSql());

            (await store.EnabledForAsync(seeded)).Should().HaveCount(
                11, "a tenant holding a membership when the gate shipped keeps every module it already had");

            // ---- The claim about THIS DATABASE, over whatever it holds --------------------------------
            var tenants = await db.Database.SqlQueryRaw<string>(
                """
                SELECT DISTINCT tenant_id AS "Value" FROM identity.tenant_membership WHERE NOT is_deleted
                """).ToListAsync();

            foreach (var tenant in tenants)
            {
                (await store.EnabledForAsync(tenant)).Should().HaveCount(
                    11, $"tenant {tenant} existed before the gate and must keep every module it already had");
            }
        }
        finally
        {
            // FK order: the membership references the user, so it goes first.
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM identity.tenant_feature WHERE tenant_id = {0}; " +
                "DELETE FROM identity.tenant_membership WHERE tenant_id = {0}; " +
                "DELETE FROM identity.user_role WHERE user_id = {1}; " +
                "DELETE FROM identity.\"user\" WHERE id = {1};", [seeded, userId]);
        }
    }

    /// <summary>
    /// Migration 0015's backfill statement, read from the migration itself.
    /// </summary>
    /// <remarks>
    /// Idempotent by construction (<c>ON CONFLICT DO NOTHING</c>), which is what makes re-running it inside a
    /// test safe against a database where it has already been applied.
    /// </remarks>
    private static string BackfillSql()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Migrations", "0015_tenant_feature_projection.sql");
        File.Exists(path).Should().BeTrue($"0015 must be copied to the test output ({path})");
        var sql = File.ReadAllText(path);

        var start = sql.IndexOf("INSERT INTO identity.tenant_feature (", StringComparison.Ordinal);
        start.Should().BeGreaterThan(
            0, "the backfill block must be findable — if 0015 was restructured, fix this test with it");
        var end = sql.IndexOf(';', start);
        end.Should().BeGreaterThan(start);

        return sql[start..(end + 1)];
    }
}
