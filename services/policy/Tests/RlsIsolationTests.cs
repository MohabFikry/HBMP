using FluentAssertions;
using Npgsql;

namespace Mersal.Policy.Tests;

/// <summary>Datastore-level tenant isolation for the policy schema (audit H1 / ADR-0011). Env-gated so
/// DB-less CI skips: POLICY_TEST_DB_OWNER (owner) + POLICY_TEST_DB_APP (NOBYPASSRLS hbmp_app).</summary>
public class RlsIsolationTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("POLICY_TEST_DB_OWNER");
    private static readonly string? App = Environment.GetEnvironmentVariable("POLICY_TEST_DB_APP");

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private static readonly Guid PolA = new("aaaaaaaa-90c0-0000-0000-000000000001");
    private static readonly Guid PolB = new("bbbbbbbb-90c0-0000-0000-000000000002");

    [Fact]
    public async Task Raw_query_under_tenant_A_guc_cannot_see_tenant_B_or_unscoped()
    {
        if (Owner is null || App is null) return;

        await Seed();
        try
        {
            (await VisiblePolicyNos(TenantA)).Should().BeEquivalentTo(["RLS-A"]);
            (await VisiblePolicyNos(TenantB)).Should().BeEquivalentTo(["RLS-B"]);
            (await VisiblePolicyNos("")).Should().BeEmpty();
        }
        finally { await Cleanup(); }
    }

    private static async Task<List<string>> VisiblePolicyNos(string tenant)
    {
        await using var conn = new NpgsqlConnection(App);
        await conn.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id',$1,false)", conn))
        {
            set.Parameters.AddWithValue(tenant);
            await set.ExecuteNonQueryAsync();
        }
        await using var q = new NpgsqlCommand("SELECT policy_no FROM policy.policy WHERE policy_no LIKE 'RLS-%' ORDER BY policy_no", conn);
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
            @"INSERT INTO policy.policy (policy_id, policy_no, tenant_id, effective_from)
              VALUES ($1,'RLS-A',$3,CURRENT_DATE), ($2,'RLS-B',$4,CURRENT_DATE)
              ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(PolA);
        cmd.Parameters.AddWithValue(PolB);
        cmd.Parameters.AddWithValue(TenantA);
        cmd.Parameters.AddWithValue(TenantB);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Cleanup()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM policy.policy WHERE policy_no LIKE 'RLS-%'", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
