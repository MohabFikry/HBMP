using FluentAssertions;
using Npgsql;

namespace Mersal.Approvals.Tests;

/// <summary>Datastore-level tenant isolation for the approvals schema (audit H1 / ADR-0011). Env-gated so
/// DB-less CI skips: APPROVALS_TEST_DB_OWNER (owner) + APPROVALS_TEST_DB_APP (NOBYPASSRLS hbmp_app).
/// Serialized with the integration suite.</summary>
[Collection("approvals-db")]
public class RlsIsolationTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("APPROVALS_TEST_DB_OWNER");
    private static readonly string? App = Environment.GetEnvironmentVariable("APPROVALS_TEST_DB_APP");

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private static readonly Guid AuthA = new("aaaaaaaa-a017-0000-0000-000000000001");
    private static readonly Guid AuthB = new("bbbbbbbb-a017-0000-0000-000000000002");

    [Fact]
    public async Task Raw_query_under_tenant_A_guc_cannot_see_tenant_B_or_unscoped()
    {
        if (Owner is null || App is null) return;

        await Seed();
        try
        {
            (await VisibleAuthNos(TenantA)).Should().BeEquivalentTo(["RLS-A"]);
            (await VisibleAuthNos(TenantB)).Should().BeEquivalentTo(["RLS-B"]);
            (await VisibleAuthNos("")).Should().BeEmpty();
        }
        finally { await Cleanup(); }
    }

    private static async Task<List<string>> VisibleAuthNos(string tenant)
    {
        await using var conn = new NpgsqlConnection(App);
        await conn.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id',$1,false)", conn))
        {
            set.Parameters.AddWithValue(tenant);
            await set.ExecuteNonQueryAsync();
        }
        await using var q = new NpgsqlCommand("SELECT auth_no FROM approvals.authorization WHERE auth_no LIKE 'RLS-%' ORDER BY auth_no", conn);
        var nos = new List<string>();
        await using var r = await q.ExecuteReaderAsync();
        while (await r.ReadAsync()) nos.Add(r.GetString(0));
        return nos;
    }

    private static async Task Seed()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO approvals.authorization (authorization_id, auth_no, tenant_id, beneficiary_id, source)
              VALUES ($1,'RLS-A',$3,gen_random_uuid(),'Manual'),
                     ($2,'RLS-B',$4,gen_random_uuid(),'Manual')
              ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(AuthA);
        cmd.Parameters.AddWithValue(AuthB);
        cmd.Parameters.AddWithValue(TenantA);
        cmd.Parameters.AddWithValue(TenantB);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Cleanup()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM approvals.authorization WHERE auth_no LIKE 'RLS-%'", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
