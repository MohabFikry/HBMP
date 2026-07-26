using FluentAssertions;
using Npgsql;

namespace Mersal.Case.Tests;

/// <summary>Datastore-level tenant isolation for the case schema (audit H1 / ADR-0011), proven independently
/// of the case-assignment ABAC anchor. Env-gated so DB-less CI skips:
///   CASE_TEST_DB_OWNER — schema owner conn string (seeds/cleans).
///   CASE_TEST_DB_APP   — NOBYPASSRLS hbmp_app conn string (role under test).</summary>
[Collection("case-db")] // serialize with the integration tests — this test writes real case_file rows
public class RlsIsolationTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("CASE_TEST_DB_OWNER");
    private static readonly string? App = Environment.GetEnvironmentVariable("CASE_TEST_DB_APP");

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private static readonly Guid CaseA = new("aaaaaaaa-ca5e-0000-0000-000000000001");
    private static readonly Guid CaseB = new("bbbbbbbb-ca5e-0000-0000-000000000002");

    [Fact]
    public async Task Raw_query_under_tenant_A_guc_cannot_see_tenant_B_or_unscoped()
    {
        if (Owner is null || App is null) return;

        await Seed();
        try
        {
            (await VisibleCaseNos(TenantA)).Should().BeEquivalentTo(["RLS-A"]);
            (await VisibleCaseNos(TenantB)).Should().BeEquivalentTo(["RLS-B"]);
            (await VisibleCaseNos("")).Should().BeEmpty();
        }
        finally { await Cleanup(); }
    }

    private static async Task<List<string>> VisibleCaseNos(string tenant)
    {
        await using var conn = new NpgsqlConnection(App);
        await conn.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id',$1,false)", conn))
        {
            set.Parameters.AddWithValue(tenant);
            await set.ExecuteNonQueryAsync();
        }
        await using var q = new NpgsqlCommand("SELECT case_no FROM \"case\".case_file WHERE case_no LIKE 'RLS-%' ORDER BY case_no", conn);
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
            @"INSERT INTO ""case"".case_file (case_id, case_no, tenant_id, beneficiary_id, category, status, priority)
              VALUES ($1,'RLS-A',$3,gen_random_uuid(),'Complex','Open','Normal'),
                     ($2,'RLS-B',$4,gen_random_uuid(),'Complex','Open','Normal')
              ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(CaseA);
        cmd.Parameters.AddWithValue(CaseB);
        cmd.Parameters.AddWithValue(TenantA);
        cmd.Parameters.AddWithValue(TenantB);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Cleanup()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM \"case\".case_file WHERE case_no LIKE 'RLS-%'", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
