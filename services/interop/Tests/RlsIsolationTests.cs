using FluentAssertions;
using Npgsql;

namespace Mersal.Interop.Tests;

/// <summary>
/// Phase 18.B2 (audit R2 S2) — datastore-level tenant isolation for the interop schema, proven independently
/// of any application predicate.
///
/// interop carried the only fail-OPEN policy in the repo — `OR current_setting('app.tenant_id', true) IS
/// NULL` — and never bound the GUC, so that disjunct was ALWAYS true and the enabled policy permitted every
/// row to every connection. 0003_tenant_rls.sql closes it and extends RLS to integration_partner and
/// inbound_staging, the quarantine table holding raw unvalidated partner payloads.
///
/// Env-gated so DB-less CI skips:
///   INTEROP_TEST_DB_OWNER — schema owner conn string (seeds/cleans).
///   INTEROP_TEST_DB_APP   — NOBYPASSRLS hbmp_app conn string (the role under test). Pointing this at a
///                        superuser would make every assertion pass while proving nothing.
/// </summary>
public class RlsIsolationTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("INTEROP_TEST_DB_OWNER");
    private static readonly string? App = Environment.GetEnvironmentVariable("INTEROP_TEST_DB_APP");

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    [SkippableFact]
    public async Task Rows_are_visible_only_under_their_own_tenant_guc()
    {
        Skip.If(Owner is null || App is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        await Seed();
        try
        {
            (await Visible(TenantA)).Should().BeEquivalentTo(["RLSI-A"]);
            (await Visible(TenantB)).Should().BeEquivalentTo(["RLSI-B"]);
            // The deny-all case: no GUC bound ⇒ ZERO rows. This is the exact assertion the fail-open policy could never have passed.
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
        await using var q = new NpgsqlCommand("SELECT dedupe_key FROM interop.fhir_create WHERE dedupe_key LIKE 'RLSI-%' ORDER BY dedupe_key", conn);
        var rows = new List<string>();
        await using var r = await q.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add(r.GetString(0));
        return rows;
    }

    private static async Task InsertAs(string sessionTenant, string stampedTenant)
    {
        await using var conn = await OpenAs(sessionTenant);
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO interop.fhir_create (dedupe_key, resource_type, tenant_id, status_code)
              VALUES ('RLSI-X', 'Patient', $1, 201)", conn);
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
            @"INSERT INTO interop.fhir_create (dedupe_key, resource_type, tenant_id, status_code)
              VALUES ('RLSI-A','Patient',$1,201),
                     ('RLSI-B','Patient',$2,201)
              ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(TenantA);
        cmd.Parameters.AddWithValue(TenantB);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Cleanup()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM interop.fhir_create WHERE dedupe_key LIKE 'RLSI-%'", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
