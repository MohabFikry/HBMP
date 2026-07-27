using FluentAssertions;
using Npgsql;

namespace Mersal.CallCentre.Tests;

/// <summary>
/// Phase 18.B2 (audit R2 X6) — datastore-level tenant isolation for the callcentre schema, proven independently
/// of any application predicate.
///
/// callcentre shipped with `tenant_id NOT NULL` on every aggregate and ZERO RLS DDL — the application
/// predicate was the only boundary between one tenant's call log and another's. 0003_tenant_rls.sql adds
/// the datastore layer; this proves it binds. The rows here are call interactions: who phoned, about which
/// beneficiary, and why.
///
/// Env-gated so DB-less CI skips:
///   CALLCENTRE_TEST_DB_OWNER — schema owner conn string (seeds/cleans).
///   CALLCENTRE_TEST_DB_APP   — NOBYPASSRLS hbmp_app conn string (the role under test). Pointing this at a
///                        superuser would make every assertion pass while proving nothing.
/// </summary>
[Collection("callcentre-db")]
public class RlsIsolationTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("CALLCENTRE_TEST_DB_OWNER");
    private static readonly string? App = Environment.GetEnvironmentVariable("CALLCENTRE_TEST_DB_APP");

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    [SkippableFact]
    public async Task Rows_are_visible_only_under_their_own_tenant_guc()
    {
        Skip.If(Owner is null || App is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        await Seed();
        try
        {
            (await Visible(TenantA)).Should().BeEquivalentTo(["RLSCC-A"]);
            (await Visible(TenantB)).Should().BeEquivalentTo(["RLSCC-B"]);
            // The deny-all case: no GUC bound ⇒ ZERO rows. A background connection sees nothing, not everything.
            (await Visible("")).Should().BeEmpty();
        }
        finally { await Cleanup(); }
    }

    [SkippableFact]
    public async Task A_write_stamped_with_another_tenant_is_rejected_by_the_policy()
    {
        Skip.If(Owner is null || App is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        try
        {
            // WITH CHECK, not just USING: reading is not the only way to cross a tenant boundary.
            var write = async () => await InsertAs(TenantA, stampedTenant: TenantB);
            await write.Should().ThrowAsync<PostgresException>()
                .Where(e => e.SqlState == PostgresErrorCodes.InsufficientPrivilege);
        }
        finally { await Cleanup(); }
    }

    private static async Task<List<string>> Visible(string tenant)
    {
        await using var conn = await OpenAs(tenant);
        await using var q = new NpgsqlCommand("SELECT call_ref FROM callcentre.call_interaction WHERE call_ref LIKE 'RLSCC-%' ORDER BY call_ref", conn);
        var rows = new List<string>();
        await using var r = await q.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add(r.GetString(0));
        return rows;
    }

    private static async Task InsertAs(string sessionTenant, string stampedTenant)
    {
        await using var conn = await OpenAs(sessionTenant);
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO callcentre.call_interaction (interaction_id, call_ref, tenant_id, agent_user_id, direction)
              VALUES (gen_random_uuid(), 'RLSCC-X', $1, gen_random_uuid(), 'Inbound')", conn);
        cmd.Parameters.AddWithValue(stampedTenant);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<NpgsqlConnection> OpenAs(string tenant)
    {
        var conn = new NpgsqlConnection(App);
        await conn.OpenAsync();
        await using var set = new NpgsqlCommand("SELECT set_config('app.tenant_id',$1,false)", conn);
        set.Parameters.AddWithValue(tenant);
        await set.ExecuteNonQueryAsync();
        return conn;
    }

    private static async Task Seed()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO callcentre.call_interaction (interaction_id, call_ref, tenant_id, agent_user_id, direction)
              VALUES (gen_random_uuid(),'RLSCC-A',$1,gen_random_uuid(),'Inbound'),
                     (gen_random_uuid(),'RLSCC-B',$2,gen_random_uuid(),'Inbound')
              ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(TenantA);
        cmd.Parameters.AddWithValue(TenantB);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Cleanup()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM callcentre.call_interaction WHERE call_ref LIKE 'RLSCC-%'", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
