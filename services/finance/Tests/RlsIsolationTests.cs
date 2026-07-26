using FluentAssertions;
using Npgsql;

namespace Mersal.Finance.Tests;

/// <summary>Datastore-level tenant isolation for the finance schema (audit H1 / ADR-0011). Env-gated so
/// DB-less CI skips: FINANCE_TEST_DB_OWNER (owner) + FINANCE_TEST_DB_APP (NOBYPASSRLS hbmp_app).
/// Serialized with the integration suite.</summary>
[Collection("finance-db")]
public class RlsIsolationTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("FINANCE_TEST_DB_OWNER");
    private static readonly string? App = Environment.GetEnvironmentVariable("FINANCE_TEST_DB_APP");

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private static readonly Guid SetA = new("aaaaaaaa-f1a0-0000-0000-000000000001");
    private static readonly Guid SetB = new("bbbbbbbb-f1a0-0000-0000-000000000002");

    [SkippableFact]
    public async Task Raw_query_under_tenant_A_guc_cannot_see_tenant_B_or_unscoped()
    {
        Skip.If(Owner is null || App is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        await Seed();
        try
        {
            (await VisibleSettlementNos(TenantA)).Should().BeEquivalentTo(["RLS-A"]);
            (await VisibleSettlementNos(TenantB)).Should().BeEquivalentTo(["RLS-B"]);
            (await VisibleSettlementNos("")).Should().BeEmpty();
        }
        finally { await Cleanup(); }
    }

    private static async Task<List<string>> VisibleSettlementNos(string tenant)
    {
        await using var conn = new NpgsqlConnection(App);
        await conn.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id',$1,false)", conn))
        {
            set.Parameters.AddWithValue(tenant);
            await set.ExecuteNonQueryAsync();
        }
        await using var q = new NpgsqlCommand("SELECT settlement_no FROM finance.settlement WHERE settlement_no LIKE 'RLS-%' ORDER BY settlement_no", conn);
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
            @"INSERT INTO finance.settlement (settlement_id, settlement_no, tenant_id, provider_id, period_start, period_end)
              VALUES ($1,'RLS-A',$3,gen_random_uuid(),CURRENT_DATE,CURRENT_DATE),
                     ($2,'RLS-B',$4,gen_random_uuid(),CURRENT_DATE,CURRENT_DATE)
              ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(SetA);
        cmd.Parameters.AddWithValue(SetB);
        cmd.Parameters.AddWithValue(TenantA);
        cmd.Parameters.AddWithValue(TenantB);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Cleanup()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM finance.settlement WHERE settlement_no LIKE 'RLS-%'", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
