using FluentAssertions;
using Npgsql;

namespace Mersal.Admin.Tests;

/// <summary>
/// Phase 18.B2 (audit R2 X6 + the fail-open policies found alongside S2) — datastore-level tenant isolation for the admin schema, proven independently
/// of any application predicate.
///
/// admin had ENABLE + policies + no binder + a superuser connection, AND every policy carried the
/// fail-OPEN `OR current_setting(...) IS NULL` escape, AND none of them were FORCEd. Four independent
/// reasons the isolation could not have worked, on the schema that decides who may reach PHI everywhere
/// else: role_binding, break_glass_grant, deprovisioned_user, session_policy.
///
/// Env-gated so DB-less CI skips:
///   ADMIN_TEST_DB_OWNER — schema owner conn string (seeds/cleans).
///   ADMIN_TEST_DB_APP   — NOBYPASSRLS hbmp_app conn string (the role under test). Pointing this at a
///                        superuser would make every assertion pass while proving nothing.
/// </summary>
[Collection("admin-db")]
public class RlsIsolationTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("ADMIN_TEST_DB_OWNER");
    private static readonly string? App = Environment.GetEnvironmentVariable("ADMIN_TEST_DB_APP");

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    [SkippableFact]
    public async Task Rows_are_visible_only_under_their_own_tenant_guc()
    {
        Skip.If(Owner is null || App is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        await Seed();
        try
        {
            (await Visible(TenantA)).Should().BeEquivalentTo(["rls-subject-A"]);
            (await Visible(TenantB)).Should().BeEquivalentTo(["rls-subject-B"]);
            // The deny-all case: no GUC bound ⇒ ZERO rows. On THIS schema that is the difference between a background job seeing no grants and seeing every tenant's.
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
        await using var q = new NpgsqlCommand("SELECT subject_user_id FROM admin.role_binding WHERE subject_user_id LIKE 'rls-subject-%' ORDER BY subject_user_id", conn);
        var rows = new List<string>();
        await using var r = await q.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add(r.GetString(0));
        return rows;
    }

    private static async Task InsertAs(string sessionTenant, string stampedTenant)
    {
        await using var conn = await OpenAs(sessionTenant);
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO admin.role_binding (binding_id, tenant_id, subject_user_id, role, granted_by, justification)
              VALUES (gen_random_uuid(), $1, 'rls-subject-X', 'org_admin', 'rls-test', 'rls-test')", conn);
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
            @"INSERT INTO admin.role_binding (binding_id, tenant_id, subject_user_id, role, granted_by, justification)
              VALUES (gen_random_uuid(),$1,'rls-subject-A','org_admin','rls-test','rls-test'),
                     (gen_random_uuid(),$2,'rls-subject-B','org_admin','rls-test','rls-test')
              ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(TenantA);
        cmd.Parameters.AddWithValue(TenantB);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Cleanup()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM admin.role_binding WHERE subject_user_id LIKE 'rls-subject-%'", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
